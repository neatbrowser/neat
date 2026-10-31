using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// Suggesting a strong password at a field where the person is choosing one. The list shows a new
/// password (pass/suggest.cs makes it), with a switch for symbols, "Another" and "Use". Nothing goes
/// into the page until "Use". The password is held only by the list while it is up; once the
/// person submits the form, saving it is the ordinary save question (win.pass.cs).
/// </summary>
public sealed partial class Win
{
    private void PasswordList(Tab tab, Windows.Foundation.Rect? anchor, bool symbols)
    {
        var pass = PasswordGen.Make(PasswordGen.Default, symbols);

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = "Suggested password",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = pass,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var sym = new CheckBox { Content = "Symbols", IsChecked = symbols, MinWidth = 0 };
        sym.Click += (s, e) => PasswordList(tab, anchor, sym.IsChecked == true);
        panel.Children.Add(sym);

        var use = new Button { Content = "Use" };
        use.Click += (s, e) => _ = Guard(() => UseSuggested(tab, pass), "using a suggested password");

        var other = new Button { Content = "Another" };
        other.Click += (s, e) => PasswordList(tab, anchor, sym.IsChecked == true);

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (s, e) => HideBubble();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(use);
        bar.Children.Add(other);
        bar.Children.Add(cancel);
        panel.Children.Add(bar);

        Present(panel, FillLife, anchor);
    }

    private async Task UseSuggested(Tab tab, string pass)
    {
        HideBubble();

        var core = tab.View.CoreWebView2;
        var origin = core is null ? null : Origin.Of(core.Source);
        if (core is null || origin is null || _cur != tab)
            return;

        string answer;
        try
        {
            answer = await core.ExecuteScriptAsync(SuggestScript.For(origin, pass));
        }
        catch (Exception ex)
        {
            // The kind only: the script text holds the password.
            Log.Write("pass", "using a suggested password failed (" + ex.GetType().Name + ")");
            Bubble("Could not fill", "The page did not accept it.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
            return;
        }

        try
        {
            tab.View.Focus(FocusState.Programmatic);
        }
        catch (Exception)
        {
        }

        var said = answer.Trim('"') switch
        {
            "pass" or "pass+confirm" => "Password filled",
            "none" => "Click the new password field first.",
            "origin" => "The page changed, so nothing was filled.",
            _ => "Could not fill this page.",
        };

        Bubble(said, string.Empty, TimeSpan.FromSeconds(2.5));
    }
}
