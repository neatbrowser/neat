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

    // ---- offering to save a card typed into a payment form ----
    //
    // The page script reports a card when the person presses a button or Enter, and only a number that
    // passes the check digit (pass/capture.cs). It is held in memory (vault/cardcapture.cs), and only when
    // the page moved on, with no card number field left, is the person asked. A form that is still there, or
    // a server that refused a request of the site, means it did not go through and nothing is asked.
    // Saving needs a click, and a vault that is shut is not asked about.

    // The card the bubble is about; kept only while the bubble is up.
    private PendingCard? _bubbleCard;

    /// <summary>A page loaded while a card is held: if it shows no card number field, the payment went through.</summary>
    private async Task AfterCard(Tab tab, Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        // Pages draw their form a moment after they load.
        await Task.Delay(700);

        if (!_cardCapture.Holds(tab))
            return;

        string answer;
        try
        {
            answer = await core.ExecuteScriptAsync(CaptureScript.HasCard);
        }
        catch (Exception)
        {
            // The tab was closed or moved on again.
            _cardCapture.Drop(tab);
            return;
        }

        if (answer == "false")
            await OfferCard(tab);
        else
            _cardCapture.Drop(tab);
    }

    private async Task OfferCard(Tab tab)
    {
        var held = _cardCapture.Take(tab);

        // The person has gone to another tab, or does not want offers here.
        if (held is null || tab != _cur || Never.Has(App.Cfg.Cur.NeverSave, held.Site))
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
                return;
            }
        }

        // A vault that is shut is not asked about for a card: the person opens it in Settings.
        if (vault.Mode is null || !vault.IsOpen || vault.FindCard(held.Number) is not null || !_cardCapture.MayOffer(held.Site))
            return;

        _bubbleCard = held;

        var last = held.Number[^4..];
        var line = "\u2022\u2022\u2022\u2022 " + last
            + (held.Expiry.Length > 0 ? "  \u00B7  expires " + held.Expiry : string.Empty)
            + "\nfrom  " + Lookalike.Shown(held.Site);

        Bubble(
            "Save card?",
            line,
            BubbleLife,
            ("Save", () => SaveCard()),
            ("Not now", () => HideBubble()));
    }

    private void SaveCard()
    {
        var card = _bubbleCard;
        if (card is null)
            return;

        var fields = new Dictionary<string, string> { ["number"] = card.Number };
        if (card.Holder.Length > 0)
            fields["holder"] = card.Holder;

        if (card.Expiry.Length > 0)
            fields["expiry"] = card.Expiry;

        if (card.Cvc.Length > 0)
            fields["cvc"] = card.Cvc;

        _bubbleCard = null;

        try
        {
            App.Vault.AddItem(ItemKind.Card, fields);
        }
        catch (InvalidOperationException)
        {
            // The vault was shut while the bubble was up.
            Bubble("Could not save", "The password vault was locked in the meantime.", TimeSpan.FromSeconds(4), ("OK", () => HideBubble()));
            return;
        }
        catch (ArgumentException)
        {
            HideBubble();
            return;
        }

        Bubble("Card saved", string.Empty, TimeSpan.FromSeconds(1.8));
    }
}
