using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// Filling a saved login into a page. Nothing fills by itself: not when a page loads, not when a
/// field is clicked. The person presses the key button in the address chip (it shows only on a page
/// that has saved logins), sees which address the login will go into, and picks one.
///
/// The rules, from the plan for the vault:
///   - Only a login saved for exactly this page's origin (scheme, host and port) is offered, so an
///     https login never reaches an http page and a look-alike address never gets one. The vault
///     checks that again when it hands the login over, and the script checks it a third time in the
///     page itself, because a page can navigate between the click and the fill.
///   - Only the top page is filled, never a frame; only fields the person can see and type in.
///   - The login goes to the page as data (one JSON object), never glued into script text.
///   - The address the login will go into is shown in the list, in a form that is hard to fake, with a
///     warning for letters outside a-z (see vault/lookalike.cs) and for an http page.
///   - Filling needs no Windows Hello: nothing is shown to anyone, it goes into the page that is open.
///     A password vault must have been unlocked, and this window locks it again after
///     <see cref="Gate.Idle"/> without a fill, if it was the one that unlocked it.
/// </summary>
public sealed partial class Win
{
    // The fill list stays up this long if nobody picks.
    private static readonly TimeSpan FillLife = TimeSpan.FromSeconds(60);

    // At most this many logins are listed for one page.
    private const int FillMax = 8;

    // The vault has been looked at, for the key button, this run.
    private bool _vaultTried;

    // This window unlocked a password vault, and when it last filled something.
    private bool _fillUnlocked;
    private DateTime _fillLast = DateTime.MinValue;
    private DispatcherQueueTimer? _fillIdle;

    // ---- the key button ----

    private void keybtn_Click(object sender, RoutedEventArgs e)
    {
        _ = Guard(OpenFill, "opening the fill list");
    }

    /// <summary>Shows the key button when the page in the selected tab has a saved login.</summary>
    private async Task UpdateKey()
    {
        var tab = _cur;
        var origin = tab is null ? null : Origin.Of(tab.Src);
        var show = false;

        if (origin is not null)
        {
            var vault = App.Vault;

            // A vault that is there is read once, so the button can show before anything else has needed
            // the vault. A vault that is not there is not made just to look.
            if (vault.Mode is null && !_vaultTried && vault.FileExists)
            {
                _vaultTried = true;

                try
                {
                    await Task.Run(() => vault.Open());
                }
                catch (Exception ex)
                {
                    Log.Error("pass", ex, "opening the vault for the key button failed");
                }
            }

            show = vault.Has(origin);
        }

        // The person may have moved on while the vault was being read.
        if (_cur == tab && !_closed)
            keybtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the list ----

    private async Task OpenFill()
    {
        var tab = _cur;
        var core = tab?.View.CoreWebView2;
        var origin = core is null ? null : Origin.Of(core.Source);
        if (tab is null || origin is null)
            return;

        var vault = App.Vault;

        if (vault.Mode is null)
        {
            try
            {
                await Task.Run(() => vault.Open());
            }
            catch (Exception ex)
            {
                Log.Error("pass", ex, "opening the vault failed");
            }

            if (_cur != tab)
                return;
        }

        if (vault.Mode is null)
        {
            Bubble("Could not open the password vault", "Details are in Settings > Passwords.", FillLife, ("OK", () => HideBubble()));
            return;
        }

        if (vault.Mode == VaultMode.Dpapi && !vault.IsOpen)
        {
            Bubble("Could not open the password vault", "Windows would not open its key. Details are in Settings > Passwords.", FillLife, ("OK", () => HideBubble()));
            return;
        }

        if (!vault.IsOpen)
        {
            AskFillUnlock(tab);
            return;
        }

        ListLogins(tab);
    }

    /// <summary>The logins saved for the page in <paramref name="tab"/>, to pick one from.</summary>
    private void ListLogins(Tab tab)
    {
        var core = tab.View.CoreWebView2;
        var origin = core is null ? null : Origin.Of(core.Source);
        if (origin is null)
            return;

        var logins = App.Vault.Find(origin).Where(l => !l.Damaged).Take(FillMax).ToList();
        if (logins.Count == 0)
        {
            Bubble("No saved login for this site", string.Empty, TimeSpan.FromSeconds(3), ("OK", () => HideBubble()));
            return;
        }

        _fillLast = DateTime.UtcNow;

        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = "Fill a saved login",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // The address the login will go into, as the person should read it.
        panel.Children.Add(new TextBlock
        {
            Text = "Into  " + Lookalike.Shown(origin),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        if (origin.StartsWith("http://", StringComparison.Ordinal))
            panel.Children.Add(Warn("This page is not secure (http): anything typed into it can be read on the way."));

        if (Lookalike.Warning(origin) is { } warning)
            panel.Children.Add(Warn(warning));

        foreach (var login in logins)
        {
            var who = login.User.Length == 0 ? "(no user name)" : login.User;
            var id = login.Id;

            var b = new Button
            {
                Content = who,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            b.Click += (s, e) => _ = Guard(() => FillWith(tab, id), "filling a login");
            panel.Children.Add(b);
        }

        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (s, e) => HideBubble();
        panel.Children.Add(cancel);

        Present(panel, FillLife);
    }

    private static TextBlock Warn(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Orange),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    // ---- unlocking a password vault ----

    private void AskFillUnlock(Tab tab)
    {
        var box = new PasswordBox { PlaceholderText = "NEAT password" };
        var message = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

        async Task Go()
        {
            var typed = box.Password;
            if (typed.Length == 0)
                return;

            message.Text = "Checking...";
            var result = await Task.Run(() => App.Vault.Unlock(typed.AsSpan()));
            box.Password = string.Empty;

            if (result == VaultResult.Ok)
            {
                _fillUnlocked = true;
                _fillLast = DateTime.UtcNow;
                StartFillIdle();

                if (_cur == tab)
                    ListLogins(tab);

                return;
            }

            message.Text = result switch
            {
                VaultResult.Wrong => "That is not the password.",
                VaultResult.Wait => "Too many wrong tries. Try again in "
                    + Math.Max(1, (int)Math.Ceiling(App.Vault.RetryAfter.TotalSeconds)) + " seconds.",
                _ => "That did not work.",
            };
        }

        box.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                _ = Guard(Go, "unlocking the vault");
        };

        var unlock = new Button { Content = "Unlock" };
        unlock.Click += (s, e) => _ = Guard(Go, "unlocking the vault");

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (s, e) => HideBubble();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(unlock);
        bar.Children.Add(cancel);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Your password vault is locked", FontSize = 14 });
        panel.Children.Add(box);
        panel.Children.Add(message);
        panel.Children.Add(bar);

        Present(panel, FillLife);
    }

    private void StartFillIdle()
    {
        _fillIdle ??= MakeFillIdle();
        _fillIdle.Start();
    }

    private DispatcherQueueTimer MakeFillIdle()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(15);
        t.IsRepeating = true;
        t.Tick += (s, e) =>
        {
            var vault = App.Vault;

            // Someone else (Settings) shut it, or switched it to Windows protection: nothing left to watch.
            if (!_fillUnlocked || vault.Mode != VaultMode.Password || !vault.IsOpen)
            {
                _fillUnlocked = false;
                t.Stop();
                return;
            }

            if (DateTime.UtcNow - _fillLast > Gate.Idle)
            {
                vault.Lock();
                _fillUnlocked = false;
                t.Stop();
            }
        };

        return t;
    }

    // ---- filling ----

    private async Task FillWith(Tab tab, Guid id)
    {
        HideBubble();

        var core = tab.View.CoreWebView2;
        if (core is null || _cur != tab)
            return;

        // The page as it is right now. The vault hands a login out only if this is exactly the origin it was
        // saved for, and the script checks again from inside the page.
        var page = core.Source;
        var origin = Origin.Of(page);

        string script;
        using (var fill = origin is null ? null : App.Vault.Fill(id, page))
        {
            if (fill is null || origin is null)
            {
                Bubble("Could not fill", "That login is not for the page that is open now.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
                return;
            }

            script = FillScript.For(origin, fill.User, fill.Password.Text());
        }

        _fillLast = DateTime.UtcNow;

        string answer;
        try
        {
            answer = await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            // The kind only: the script text holds the password.
            Log.Write("pass", "filling failed (" + ex.GetType().Name + ")");
            Bubble("Could not fill", "The page did not accept it.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
            return;
        }

        var said = answer.Trim('"') switch
        {
            "user+pass" => "Filled",
            "pass" => "Password filled",
            "user" => "User name filled. Fill again on the next page for the password.",
            "none" => "There is no login form here that can be filled.",
            "origin" => "The page changed, so nothing was filled.",
            "frame" => "Nothing was filled.",
            _ => "Could not fill this page.",
        };

        Bubble(said, string.Empty, TimeSpan.FromSeconds(2.5));
    }
}
