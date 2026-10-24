using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Saving passwords: notices a login on a page and offers to save it in the vault. The page
/// script is in pass/capture.cs, what is held while it is found out whether the login worked is
/// in vault/capture.cs, and this file connects them to the window.
///
/// How a login travels:
///   1. The person presses Sign in (or Enter). The page script reports the user name and
///      password, and only for a real click or key. The window holds them for a few seconds.
///   2. The page moves on. If the next page has no password field, or the field went away
///      without a new page, the login is taken to have worked. If the form is still there it
///      did not, and the held login is dropped.
///   3. Only then is the person asked, in a small bubble under the address, with Save, Not now and
///      Never for this site. Nothing is saved without a click, and nothing blocks the page.
///
/// A login is only accepted from the page that is open in the tab, by origin, never from a frame
/// inside it. A password is not written to the log: failures here are logged by kind only.
/// </summary>
public sealed partial class Win
{
    // How long the bubble stays if nobody answers it.
    private static readonly TimeSpan BubbleLife = TimeSpan.FromSeconds(45);

    private readonly Capture _capture = new();
    private DispatcherQueueTimer? _bubbleTimer;

    // The login the bubble is about, and the saved entry it would update (null: it would be a new one).
    // Kept only while the bubble is up.
    private Pending? _bubbleLogin;
    private Guid? _bubbleEntry;

    // ---- wiring ----

    /// <summary>Adds the page script. It must be in place before the first page loads.</summary>
    private async Task AddPassScript(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CaptureScript.For(_tok));
        }
        catch (Exception ex)
        {
            Log.Error("pass", ex, "the save-password script could not be added");
        }
    }

    /// <summary>Called for every tab when its web view is ready.</summary>
    private void HookPass(Tab tab, CoreWebView2 core)
    {
        // WebView2 has a password saver and an autofill of its own. The vault is meant to be the
        // one place passwords live, so both are switched off.
        try
        {
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
        }
        catch (Exception ex)
        {
            Log.Error("pass", ex, "could not switch off the built-in password saving");
        }

        // A page finished loading while a login is held: find out if the login worked.
        core.NavigationCompleted += (s, e) =>
        {
            if (e.IsSuccess && _capture.Holds(tab))
                _ = Guard(() => AfterLogin(tab, core), "after a login");
        };
    }

    /// <summary>
    /// Every message a page posts comes through here. A page can post anything, not only text,
    /// and TryGetWebMessageAsString throws for anything that is not text: that must never leave
    /// this method, because an exception in an event handler ends the app.
    /// </summary>
    private void OnMessage(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? text;
            try
            {
                text = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            if (text is null)
                return;

            if (text.StartsWith("neatpw:", StringComparison.Ordinal))
                OnLogin(tab, e.Source, text);
            else
                OnKey(tab, text);
        }
        catch (Exception ex)
        {
            // The kind only: what a page sent may hold a password and an exception message may quote it.
            Log.Write("pass", "a message from a page could not be handled (" + ex.GetType().Name + ")");
        }
    }

    // ---- what the page reports ----

    private void OnLogin(Tab tab, string source, string text)
    {
        var head = "neatpw:" + _tok + ":";
        if (!text.StartsWith(head, StringComparison.Ordinal) || text.Length > 4096)
            return;

        // Only the page open in this tab. A frame inside it reports its own address, and a tab that has
        // moved on is not the page the person typed into.
        var core = tab.View.CoreWebView2;
        var origin = Origin.Of(source);
        if (core is null || origin is null || origin != Origin.Of(core.Source))
            return;

        using var doc = JsonDocument.Parse(text.AsMemory(head.Length), new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("t", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            return;

        switch (kind.GetString())
        {
            case "offer":
                var user = JsonText(root, "u");
                var pass = JsonText(root, "p");
                if (user is not null && pass is not null)
                    _capture.Hold(tab, origin, user, pass);

                break;

            case "user":
                // The first step of a two-step login: kept until the password step arrives.
                var first = JsonText(root, "u");
                if (first is not null)
                    _capture.HoldUser(tab, origin, first);

                break;

            case "gone":
                // The password field went away without a new page: a login on a page that works by script.
                if (_capture.Holds(tab))
                    _ = Guard(() => Offer(tab), "offering a login");

                break;
        }
    }

    private static string? JsonText(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>A page loaded while a login was held: if it shows no password field, the login worked.</summary>
    private async Task AfterLogin(Tab tab, CoreWebView2 core)
    {
        // Pages draw their form a moment after they load.
        await Task.Delay(700);

        if (!_capture.Holds(tab))
            return;

        string answer;
        try
        {
            answer = await core.ExecuteScriptAsync(CaptureScript.HasPassword);
        }
        catch (Exception)
        {
            // The tab was closed or moved on again.
            _capture.Drop(tab);
            return;
        }

        if (answer == "false")
            await Offer(tab);
        else
            _capture.Drop(tab);
    }

    // ---- deciding what to offer ----

    private async Task Offer(Tab tab)
    {
        var held = _capture.Take(tab);

        // The person has gone to another tab: asking there would be out of place.
        if (held is null || tab != _cur)
            return;

        if (Never.Has(App.Cfg.Cur.NeverSave, held.Site))
            return;

        var vault = App.Vault;

        // The vault is read the first time it is needed (the Passwords page does the same).
        if (vault.Mode is null)
        {
            try
            {
                await Task.Run(() => vault.Open());
            }
            catch (Exception ex)
            {
                Log.Error("pass", ex, "opening the vault failed");
                return;
            }
        }

        // A file that cannot be used: nothing can be saved, and the Passwords page says why.
        if (vault.Mode is null || !_capture.MayOffer(held.Site))
            return;

        if (!vault.IsOpen)
        {
            // A password vault that is shut cannot take a new login. (A Windows-protected one that
            // is shut has lost its key: the Passwords page says so, and nothing is offered here.)
            if (vault.Mode == VaultMode.Password)
                AskUnlock();

            return;
        }

        var match = vault.Lookup(held.Site, held.User, held.Password);

        // Already saved exactly like this: nothing to offer.
        if (match.SamePassword)
            return;

        AskSave(held, match.Id);
    }

    // ---- the bubble ----

    private void AskSave(Pending login, Guid? entry)
    {
        _bubbleLogin = login;
        _bubbleEntry = entry;

        var who = login.User.Length == 0 ? "(no user name)" : login.User;
        var update = entry is not null;

        Bubble(
            update ? "Update password?" : "Save password?",
            who + "  on  " + login.Site + "\nPassword: \u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022",
            BubbleLife,
            (update ? "Update" : "Save", () => SaveLogin()),
            ("Not now", () => HideBubble()),
            ("Never for this site", () => NeverHere()));
    }

    private void AskUnlock()
    {
        Bubble(
            "Your password vault is locked",
            "Unlock it in Settings > Passwords, then sign in again to save this login.",
            BubbleLife,
            ("Open Settings", () => { HideBubble(); ShowOpts(); }),
            ("Not now", () => HideBubble()));
    }

    private void SaveLogin()
    {
        var login = _bubbleLogin;
        var entry = _bubbleEntry;
        if (login is null)
            return;

        try
        {
            if (entry is { } id)
                App.Vault.Update(id, login.Site, login.User, login.Password);
            else
                App.Vault.Add(login.Site, login.User, login.Password);
        }
        catch (InvalidOperationException)
        {
            // The vault was shut while the bubble was up.
            Bubble("Could not save", "The password vault was locked in the meantime.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
            _bubbleLogin = null;
            return;
        }

        Bubble(entry is null ? "Password saved" : "Password updated", string.Empty, TimeSpan.FromSeconds(1.8));
    }

    private void NeverHere()
    {
        var login = _bubbleLogin;
        HideBubble();

        if (login is null)
            return;

        if (Never.Add(App.Cfg.Cur.NeverSave, login.Site))
            App.Cfg.Save();
    }

    /// <summary>
    /// Shows the bubble under the address: a title, a line, and buttons. The login it is about stays in
    /// memory only while it is up; any button that ends it, and the timer, drop it.
    /// </summary>
    private void Bubble(string title, string line, TimeSpan life, params (string Text, Action Run)[] actions)
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });

        if (line.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (actions.Length > 0)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            foreach (var (text, run) in actions)
            {
                var b = new Button { Content = text };
                b.Click += (s, e) =>
                {
                    try
                    {
                        run();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("pass", ex, "a bubble button failed");
                        HideBubble();
                    }
                };

                bar.Children.Add(b);
            }

            panel.Children.Add(bar);
        }

        bubble.Child = panel;
        bubble.Visibility = Visibility.Visible;

        _bubbleTimer ??= MakeBubbleTimer();
        _bubbleTimer.Stop();
        _bubbleTimer.Interval = life;
        _bubbleTimer.Start();
    }

    private DispatcherQueueTimer MakeBubbleTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Tick += (s, e) => HideBubble();
        return t;
    }

    /// <summary>Takes the bubble down and lets go of the login it held.</summary>
    private void HideBubble()
    {
        _bubbleTimer?.Stop();
        _bubbleLogin = null;
        _bubbleEntry = null;

        bubble.Child = null;
        bubble.Visibility = Visibility.Collapsed;
    }

    /// <summary>When the window closes: forgets held logins without touching any control.</summary>
    private void ForgetLogins()
    {
        _bubbleTimer?.Stop();
        _bubbleLogin = null;
        _bubbleEntry = null;
        _capture.Clear();
    }

    private static async Task Guard(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Write("pass", "failed " + what + " (" + ex.GetType().Name + ")");
        }
    }
}
