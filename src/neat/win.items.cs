using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// Filling a saved card or identity (vault/items.cs) into the form the person is looking at. Like a saved
/// login, nothing fills by itself: the key at a card or contact field (win.field.cs) opens a list, and
/// a pick fills. The list shows only a name and a short line (the last four digits of a card, the name and
/// town of an identity), never the whole secret. The fields go to the page as data (pass/form.cs), which
/// checks the page's origin from inside.
///
/// A card or identity belongs to no site, so the vault does not tie it to the page's origin the way it
/// does a login. What stands in the way of a page that is not what it seems is that the person picks it,
/// from a list drawn by NEAT, under the field, with the page's address in it as it should be read.
/// </summary>
public sealed partial class Win
{
    private void ListItems(Tab tab, Windows.Foundation.Rect? anchor, string kind)
    {
        var core = tab.View.CoreWebView2;
        var origin = core is null ? null : Origin.Of(core.Source);
        if (origin is null)
            return;

        var card = kind == ItemKind.Card;
        var items = App.Vault.Items(kind).Where(i => !i.Damaged).Take(FillMax).ToList();
        if (items.Count == 0)
        {
            Bubble(card ? "No saved card" : "No saved identity", "Add one in Settings > Passwords.", TimeSpan.FromSeconds(3), ("OK", () => HideBubble()));
            return;
        }

        _fillLast = DateTime.UtcNow;

        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = card ? "Fill a saved card" : "Fill a saved identity",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // Where it will go, as the person should read it.
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

        foreach (var item in items)
        {
            var id = item.Id;

            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = item.Label, TextTrimming = TextTrimming.CharacterEllipsis });
            if (item.Summary.Length > 0)
                content.Children.Add(new TextBlock { Text = item.Summary, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });

            var b = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            b.Click += (s, e) => _ = Guard(() => FillItem(tab, id, kind), "filling a saved item");
            panel.Children.Add(b);
        }

        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (s, e) => HideBubble();
        panel.Children.Add(cancel);

        Present(panel, FillLife, anchor);
    }

    private async Task FillItem(Tab tab, Guid id, string kind)
    {
        HideBubble();

        var core = tab.View.CoreWebView2;
        if (core is null || _cur != tab)
            return;

        var origin = Origin.Of(core.Source);
        var fields = origin is null ? null : App.Vault.FillItem(id, kind);
        if (fields is null || origin is null)
        {
            Bubble("Could not fill", "That item could not be opened.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
            return;
        }

        _fillLast = DateTime.UtcNow;

        string answer;
        try
        {
            answer = await core.ExecuteScriptAsync(FormScript.For(origin, kind, fields));
        }
        catch (Exception ex)
        {
            // The kind only: the script text holds the card.
            Log.Write("pass", "filling an item failed (" + ex.GetType().Name + ")");
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

        var said = answer.Trim('"');
        var text = said switch
        {
            "none" => "There is nothing here that can be filled.",
            "origin" => "The page changed, so nothing was filled.",
            "frame" => "Nothing was filled.",
            "error" => "Could not fill this page.",
            _ => said.StartsWith("form:", StringComparison.Ordinal) ? "Filled" : "Could not fill this page.",
        };

        Bubble(text, string.Empty, TimeSpan.FromSeconds(2.5));
    }
}
