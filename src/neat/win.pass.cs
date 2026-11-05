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
///      did not, and the held login is dropped. A server that answers a request of the same site
///      with "refused" (401, 403 and the like) drops it sooner (vault/loginnet.cs); a good answer
///      never offers anything by itself.
///   3. Only then is the person asked, in a small bubble under the address, with Save, Not now and
///      Never for this site. Nothing is saved without a click, and nothing blocks the page.
///
/// A login is only accepted from the page that is open in the tab, by origin, or from a frame right under
/// it through the frame's own channel (win.frames.cs), with the frame's own address. A password is not
/// written to the log: failures here are logged by kind only.
/// </summary>
public sealed partial class Win
{
    // How long the bubble stays if nobody answers it.
    private static readonly TimeSpan BubbleLife = TimeSpan.FromSeconds(45);

    private readonly Capture _capture = new();
    private readonly CardCapture _cardCapture = new();
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

        // A server that refuses the login says so before the page does: drop the held login at once
        // (vault/loginnet.cs decides). It is only ever used to drop, never to offer. The held-login test
        // comes first, so every other response costs one dictionary lookup. Nothing of the request or
        // the answer is logged but the status: an address or a body may hold a secret.
        core.WebResourceResponseReceived += (s, e) =>
        {
            try
            {
                var login = _capture.Holds(tab);
                var card = _cardCapture.Holds(tab);
                if (!login && !card)
                    return;

                var status = e.Response.StatusCode;
                if (login && _capture.Answered(tab, e.Request.Method, e.Request.Uri, status))
                    Log.Write("pass", "a held login was dropped: the server answered " + status);

                if (card && _cardCapture.Answered(tab, e.Request.Method, e.Request.Uri, status))
                    Log.Write("pass", "a held card was dropped: the server answered " + status);
            }
            catch (Exception ex)
            {
                Log.Write("pass", "an answer could not be judged (" + ex.GetType().Name + ")");
            }
        };

        // The key and the list at a field belong to the page that was there.
        core.NavigationStarting += (s, e) =>
        {
            if (tab == _cur)
                HideField();
        };

        // Frames the page makes (win.frames.cs).
        core.FrameCreated += (s, e) => HookFrame(tab, e.Frame);

        // A page finished loading while a login is held: find out if the login worked.
        core.NavigationCompleted += (s, e) =>
        {
            if (e.IsSuccess && _capture.Holds(tab))
                _ = Guard(() => AfterLogin(tab, core), "after a login");

            if (e.IsSuccess && _cardCapture.Holds(tab))
                _ = Guard(() => AfterCard(tab, core), "after a card");
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

        // Only the page open in this tab. A frame reports through its own channel (win.frames.cs), and a
        // tab that has moved on is not the page the person typed into.
        var core = tab.View.CoreWebView2;
        var origin = Origin.Of(source);
        if (core is null || origin is null || origin != Origin.Of(core.Source))
            return;

        Dispatch(tab, origin, null, text, head);
    }

    /// <summary>
    /// What a page or a frame reported. <paramref name="origin"/> is the address of whoever sent it, as WebView2 says
    /// (for a frame, <paramref name="frame"/> is set). A frame speaks only through its own channel and marks what
    /// it says (fr); anything that does not fit that is dropped.
    /// </summary>
    private void Dispatch(Tab tab, string origin, CoreWebView2Frame? frame, string text, string head)
    {
        using var doc = JsonDocument.Parse(text.AsMemory(head.Length), new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("t", out var kind)
            || kind.ValueKind != JsonValueKind.String)
            return;

        if (root.TryGetProperty("fr", out _) != (frame is not null))
            return;

        var core = tab.View.CoreWebView2;
        var top = core is null ? null : Origin.Of(core.Source);
        if (top is null)
            return;

        switch (kind.GetString())
        {
            case "offer":
                var user = JsonText(root, "u");
                var pass = JsonText(root, "p");

                // Set only by a change-password form: the password the person had before.
                var old = JsonText(root, "o");
                if (user is not null && pass is not null && _capture.Hold(tab, origin, user, pass, old))
                    SetFrame(_loginFrame, tab, frame);

                break;

            case "user":
                // The first step of a two-step login: kept until the password step arrives.
                var first = JsonText(root, "u");
                if (first is not null)
                    _capture.HoldUser(tab, origin, first);

                break;

            case "card":
                // A card typed into a payment form, held until it is known whether the form went through (win.items.cs).
                // It is held for the top page's address, the shop the person is paying, also when typed in a frame.
                var number = JsonText(root, "n");
                if (number is not null
                    && _cardCapture.Hold(tab, top, number, JsonText(root, "h") ?? string.Empty, JsonText(root, "e") ?? string.Empty, JsonText(root, "c") ?? string.Empty))
                    SetFrame(_cardFrame, tab, frame);

                break;

            case "cardgone":
                // The card number field went away without a new page (said by whoever reported the card).
                if (_cardCapture.Holds(tab) && SameFrame(_cardFrame, tab, frame))
                    _ = Guard(() => OfferCard(tab), "offering a card");

                break;

            case "field":
                // Where the field with the focus is, for the key drawn over it (win.field.cs, win.frames.cs).
                if (frame is null)
                    OnField(tab, origin, root);
                else
                    OnFrameField(tab, frame, origin, root);

                break;

            case "frect":
                // Where a frame's field is on the page: only the top page can tell.
                if (frame is null)
                    OnFrameRect(root);

                break;

            case "gone":
                // The password field went away without a new page: a login on a page that works by script.
                if (_capture.Holds(tab) && SameFrame(_loginFrame, tab, frame))
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
            // In the frame that reported the login, if it did and the frame is still there.
            answer = await RunHeld(_loginFrame, tab, core, CaptureScript.HasPassword);
        }
        catch (Exception)
        {
            // The tab was closed or moved on again.
            _capture.Drop(tab);
            _loginFrame.Remove(tab);
            return;
        }

        if (answer == "false")
            await Offer(tab);
        else
        {
            _capture.Drop(tab);
            _loginFrame.Remove(tab);
        }
    }

    // ---- deciding what to offer ----

    private async Task Offer(Tab tab)
    {
        var held = _capture.Take(tab);
        var framed = _loginFrame.Remove(tab);

        // The person has gone to another tab: asking there would be out of place.
        if (held is null || tab != _cur)
            return;

        if (Never.Has(App.Cfg.Cur.NeverSave, held.Site))
            return;

        // The page above, when the login was typed in a frame of it: said in the question.
        var top = tab.View.CoreWebView2 is { } c ? Origin.Of(c.Source) : null;

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

        // A change-password form: the new password replaces the one saved entry that had the old one.
        // The page rarely shows the user name there, so the entry's own is kept. If several entries had
        // that password, or none, it is treated as an ordinary login below.
        if (held.Old is not null)
        {
            var targets = vault.MatchOld(held.Site, held.User, held.Old);
            if (targets.Count == 1)
            {
                var entry = vault.Find(held.Site).FirstOrDefault(l => l.Id == targets[0]);
                var named = held with { User = held.User.Length > 0 ? held.User : entry?.User ?? string.Empty };

                if (!vault.Lookup(named.Site, named.User, named.Password).SamePassword)
                    AskSave(named, targets[0], framed ? top : null);

                return;
            }
        }

        var match = vault.Lookup(held.Site, held.User, held.Password);

        // Already saved exactly like this: nothing to offer.
        if (match.SamePassword)
            return;

        AskSave(held, match.Id, framed ? top : null);
    }

    // ---- the bubble ----

    private void AskSave(Pending login, Guid? entry, string? embeddedIn = null)
    {
        _bubbleLogin = login;
        _bubbleEntry = entry;

        var who = login.User.Length == 0 ? "(no user name)" : login.User;
        var update = entry is not null;

        Bubble(
            update ? "Update password?" : "Save password?",
            who + "  on  " + login.Site
                + (embeddedIn is null ? string.Empty : "\n(typed in a frame of  " + embeddedIn + ")")
                + "\nPassword: \u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022",
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

        // The page now has a saved login: the key button shows.
        _ = Guard(UpdateKey, "updating the key button");
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

        Present(panel, life);
    }

    /// <summary>
    /// Puts a finished panel in the bubble and starts the timer that takes it down. The save question
    /// and the fill list (win.fill.cs) both use it, so only one of them is up at a time.
    /// </summary>
    private void Present(UIElement panel, TimeSpan life, Windows.Foundation.Rect? anchor = null)
    {
        bubble.Child = panel;
        bubble.Visibility = Visibility.Visible;

        // Under a field of the page, or in the usual place under the address.
        if (anchor is { } a)
        {
            var x = Math.Clamp(a.X, 8d, Math.Max(8d, root.ActualWidth - bubble.Width - 8));
            bubble.HorizontalAlignment = HorizontalAlignment.Left;
            bubble.Margin = new Thickness(x, a.Y + a.Height + 4, 0, 0);
            _bubbleAnchored = true;
        }
        else
        {
            PlaceBubble();
        }

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

    // The bubble is hanging under a field of the page (the fill list), not under the address.
    private bool _bubbleAnchored;

    private void PlaceBubble()
    {
        bubble.HorizontalAlignment = HorizontalAlignment.Center;
        bubble.Margin = new Thickness(0, 40, 0, 0);
        _bubbleAnchored = false;
    }

    /// <summary>Takes the bubble down and lets go of the login it held.</summary>
    private void HideBubble()
    {
        _bubbleTimer?.Stop();
        _bubbleLogin = null;
        _bubbleEntry = null;
        _bubbleCard = null;
        PlaceBubble();

        bubble.Child = null;
        bubble.Visibility = Visibility.Collapsed;
    }

    /// <summary>When the window closes: forgets held logins without touching any control.</summary>
    private void ForgetLogins()
    {
        _bubbleTimer?.Stop();
        _bubbleLogin = null;
        _bubbleEntry = null;
        _bubbleCard = null;
        _capture.Clear();
        _cardCapture.Clear();
        _loginFrame.Clear();
        _cardFrame.Clear();
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
