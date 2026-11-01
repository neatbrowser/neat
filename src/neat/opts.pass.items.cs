using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The Passwords page, fourth part: saved cards and identities (vault/items.cs). They are listed with a
/// name and a short line (the last four digits of a card), and editing or deleting one passes the gate,
/// as for a login. A task opens as a card on the page itself, like adding a login.
/// </summary>
public sealed partial class Opts
{
    private List<ItemInfo> _cards = new();
    private List<ItemInfo> _identities = new();

    // What the form of a kind asks for: the saved field, its title, a note, and whether it is hidden as typed.
    private static readonly (string Key, string Title, string? Note, bool Secret)[] CardForm =
    {
        ("label", "Name", "How it is listed. Left empty, it is named after the number.", false),
        ("holder", "Name on card", null, false),
        ("number", "Card number", "12 to 19 digits.", false),
        ("expiry", "Expires", "Month and year, like 08/27.", false),
        ("cvc", "Security code", "The 3 or 4 digits on the card. Optional.", true),
    };

    private static readonly (string Key, string Title, string? Note, bool Secret)[] IdentityForm =
    {
        ("label", "Name", "How it is listed. Left empty, it is named after the person.", false),
        ("first", "First name", null, false),
        ("last", "Last name", null, false),
        ("email", "Email", null, false),
        ("phone", "Phone", null, false),
        ("org", "Company", null, false),
        ("street", "Street", null, false),
        ("street2", "Street, line 2", null, false),
        ("city", "City", null, false),
        ("state", "State or province", null, false),
        ("zip", "Postal code", null, false),
        ("country", "Country", null, false),
    };

    private static string Noun(string kind)
    {
        return kind == ItemKind.Card ? "card" : "identity";
    }

    private UIElement ItemsCard(string kind)
    {
        var items = kind == ItemKind.Card ? _cards : _identities;
        var rows = new List<UIElement>
        {
            Block.Row(_skin, "Add a " + Noun(kind),
                kind == ItemKind.Card
                    ? "Saved here, sealed like a password. It is filled into a payment form only when you pick it from the key at the field."
                    : "Saved here, sealed like a password. It is filled into a form only when you pick it from the key at the field.",
                Btn("Add", () => Start(() => TaskItem(kind, null, null)))),
        };

        foreach (var item in items)
        {
            rows.Add(new Border { Height = 1, Background = _skin.Line });
            rows.Add(ItemRow(item));
        }

        return Block.Card(_skin, rows.ToArray());
    }

    private UIElement ItemRow(ItemInfo it)
    {
        var usable = _prot != Protection.None;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(IconBtn("", "Edit", () => EditItem(it), usable && !it.Damaged));
        tools.Children.Add(IconBtn("", "Delete", () => DeleteItem(it), usable));

        return Block.Row(_skin, it.Label, it.Damaged ? "This entry does not check out: it was changed outside NEAT." : it.Summary, tools);
    }

    private async Task EditItem(ItemInfo it)
    {
        var access = await Authorize("Edit the saved " + Noun(it.Kind) + " " + it.Label);
        if (access is null || _body is null)
            return;

        var fields = App.Vault.ReadItem(it.Id, access);
        if (fields is null)
        {
            Say("This entry could not be opened. It may have been changed outside NEAT.");
            return;
        }

        TaskItem(it.Kind, it, fields);
    }

    private async Task DeleteItem(ItemInfo it)
    {
        var access = await Authorize("Delete the saved " + Noun(it.Kind) + " " + it.Label);
        if (access is null || _body is null)
            return;

        TaskItemDelete(it);
    }

    private void TaskItem(string kind, ItemInfo? existing, Dictionary<string, string>? values)
    {
        var form = kind == ItemKind.Card ? CardForm : IdentityForm;
        var boxes = new Dictionary<string, Control>();
        var rows = new List<UIElement>();

        foreach (var (key, title, note, secret) in form)
        {
            var text = values is not null && values.TryGetValue(key, out var have) ? have : string.Empty;

            Control box = secret
                ? new PasswordBox { Width = 260, Password = text }
                : new TextBox { Width = 260, Text = text };

            boxes[key] = box;
            rows.Add(Block.Row(_skin, title, note, box));
        }

        string Read(string key)
        {
            return boxes[key] switch
            {
                PasswordBox p => p.Password,
                TextBox t => t.Text,
                _ => string.Empty,
            };
        }

        async Task Save()
        {
            var fields = form.ToDictionary(f => f.Key, f => Read(f.Key));

            try
            {
                if (existing is null)
                {
                    App.Vault.AddItem(kind, fields);
                }
                else if (!App.Vault.UpdateItem(existing.Id, fields))
                {
                    Fail("That " + Noun(kind) + " could not be changed. It may have been deleted.");
                    return;
                }
            }
            catch (ArgumentException ex)
            {
                // The sentence is meant for the person (vault/items.cs).
                Fail(ex.Message);
                return;
            }

            CloseTask();
            Say("Saved.");
            await RefreshPass();
        }

        ShowTask(
            (existing is null ? "Add a " : "Edit a ") + Noun(kind),
            rows,
            Btn("Save", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }

    private void TaskItemDelete(ItemInfo it)
    {
        async Task Save()
        {
            var access = await Authorize("Delete the saved " + Noun(it.Kind) + " " + it.Label);
            if (access is null || _body is null)
                return;

            App.Vault.RemoveItem(it.Id);

            CloseTask();
            Say("Deleted.");
            await RefreshPass();
        }

        ShowTask(
            "Delete a " + Noun(it.Kind),
            new UIElement[] { Explain("Delete " + it.Label + "? This cannot be undone.") },
            Btn("Delete", Save),
            Btn("Cancel", () => Start(CloseTask)));
    }
}
