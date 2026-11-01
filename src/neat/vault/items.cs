using System.Text;
using System.Text.Json;

namespace neat;

/// <summary>The kinds of saved item besides logins.</summary>
public static class ItemKind
{
    public const string Card = "card";
    public const string Identity = "identity";

    public static bool Known(string? kind)
    {
        return kind is Card or Identity;
    }
}

/// <summary>
/// What a saved card or identity may hold, and the cleaning that makes what a person typed into what is
/// kept. Nothing here touches the vault or the screen, so it is checked on its own.
///
/// Card: label, holder, number (digits only, 12 to 19), expiry (MM/YY), cvc (3 or 4 digits).
/// Identity: label, first, last, email, phone, org, street, street2, city, state, zip, country.
/// A field that is empty is left out. The label is made from the rest when it is empty.
/// </summary>
public static class ItemRules
{
    public const int MaxText = 256;

    private static readonly string[] CardFields = { "label", "holder", "number", "expiry", "cvc" };
    private static readonly string[] IdentityFields =
    {
        "label", "first", "last", "email", "phone", "org", "street", "street2", "city", "state", "zip", "country",
    };

    /// <summary>The fields of a kind, in the order a form shows them; null for a kind that is not one.</summary>
    public static IReadOnlyList<string>? FieldsOf(string? kind)
    {
        return kind switch
        {
            ItemKind.Card => CardFields,
            ItemKind.Identity => IdentityFields,
            _ => null,
        };
    }

    /// <summary>
    /// The fields of an item as they are kept. Throws ArgumentException, with a sentence fit to show,
    /// for anything that cannot be a card or identity.
    /// </summary>
    public static Dictionary<string, string> Clean(string kind, IReadOnlyDictionary<string, string> fields)
    {
        var known = FieldsOf(kind) ?? throw new ArgumentException("That is not a kind of item.");
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in known)
        {
            if (!fields.TryGetValue(name, out var raw) || raw is null)
                continue;

            var value = raw.Trim();
            if (value.Length == 0)
                continue;

            if (value.Length > MaxText)
                throw new ArgumentException("One of the fields is too long.");

            clean[name] = value;
        }

        if (kind == ItemKind.Card)
        {
            var typed = clean.GetValueOrDefault("number", string.Empty);
            var digits = Digits(typed);
            if (digits.Length < 12 || digits.Length > 19 || !typed.All(c => char.IsAsciiDigit(c) || c is ' ' or '-'))
                throw new ArgumentException("The card number must be 12 to 19 digits.");

            clean["number"] = digits;

            if (clean.TryGetValue("expiry", out var expiry))
            {
                clean["expiry"] = Expiry(expiry) ?? throw new ArgumentException("The expiry must be a month and a year, like 08/27.");
            }

            if (clean.TryGetValue("cvc", out var cvc) && (cvc.Length is < 3 or > 4 || !cvc.All(char.IsAsciiDigit)))
                throw new ArgumentException("The security code is 3 or 4 digits.");
        }
        else if (clean.Keys.All(k => k == "label"))
        {
            throw new ArgumentException("Fill in at least one field.");
        }

        if (!clean.ContainsKey("label"))
            clean["label"] = DefaultLabel(kind, clean);

        return clean;
    }

    /// <summary>Only the digits of a text (a card number is typed with spaces or hyphens).</summary>
    public static string Digits(string? text)
    {
        return text is null ? string.Empty : new string(text.Where(char.IsAsciiDigit).ToArray());
    }

    /// <summary>A month and year as MM/YY, from 8/27, 08/2027, 0827, 08-27 and the like; null when it is not one.</summary>
    public static string? Expiry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.Trim().Replace(" ", string.Empty);
        string month;
        string year;

        var cut = t.IndexOfAny(new[] { '/', '-', '.' });
        if (cut >= 0)
        {
            month = t[..cut];
            year = t[(cut + 1)..];
        }
        else if (t.Length == 4)
        {
            month = t[..2];
            year = t[2..];
        }
        else if (t.Length == 6)
        {
            month = t[..2];
            year = t[2..];
        }
        else
        {
            return null;
        }

        if (month.Length is < 1 or > 2 || !month.All(char.IsAsciiDigit) || !year.All(char.IsAsciiDigit) || (year.Length != 2 && year.Length != 4))
            return null;

        var m = int.Parse(month);
        if (m < 1 || m > 12)
            return null;

        return m.ToString("00") + "/" + year[^2..];
    }

    /// <summary>The line under an item's name: not enough to use the item.</summary>
    public static string Summary(string kind, IReadOnlyDictionary<string, string> f)
    {
        var parts = new List<string>();

        if (kind == ItemKind.Card)
        {
            var number = f.GetValueOrDefault("number", string.Empty);
            if (number.Length >= 4)
                parts.Add("•••• " + number[^4..]);

            if (f.TryGetValue("expiry", out var expiry))
                parts.Add("expires " + expiry);

            if (f.TryGetValue("holder", out var holder))
                parts.Add(holder);
        }
        else
        {
            var name = string.Join(" ", new[] { f.GetValueOrDefault("first"), f.GetValueOrDefault("last") }.Where(s => !string.IsNullOrEmpty(s)));
            if (name.Length > 0)
                parts.Add(name);

            foreach (var key in new[] { "city", "email", "country" })
            {
                if (f.TryGetValue(key, out var value))
                {
                    parts.Add(value);
                    break;
                }
            }
        }

        return string.Join("  ·  ", parts);
    }

    private static string DefaultLabel(string kind, IReadOnlyDictionary<string, string> f)
    {
        if (kind == ItemKind.Card)
        {
            var number = f.GetValueOrDefault("number", string.Empty);
            return number.Length >= 4 ? "Card •••• " + number[^4..] : "Card";
        }

        var name = string.Join(" ", new[] { f.GetValueOrDefault("first"), f.GetValueOrDefault("last") }.Where(s => !string.IsNullOrEmpty(s)));
        return name.Length > 0 ? name : "Identity";
    }
}

public sealed partial class Vault
{
    // ---- cards and identities ----
    //
    // Sealed like the fields of a login, one block per item, to the item's id and kind. Unlike a login a
    // card or identity belongs to no site: it is filled wherever the person picks it in the list, never
    // by itself. The lock is the same one as for logins.

    private VaultItemRow? FindItem(Guid id)
    {
        var text = id.ToString("N");
        return _file!.Items.FirstOrDefault(r => r.Id == text);
    }

    /// <summary>Whether any item of a kind is saved. Like <see cref="Has"/> it needs no key, so it works while the vault is shut.</summary>
    public bool HasItems(string kind)
    {
        lock (_gate)
        {
            return _file is not null && _file.Items.Any(r => r.Kind == kind);
        }
    }

    /// <summary>The saved items of a kind, newest change first. Nothing in them is a whole secret.</summary>
    public IReadOnlyList<ItemInfo> Items(string kind)
    {
        lock (_gate)
        {
            var key = Key();

            return _file!.Items
                .Where(r => r.Kind == kind)
                .OrderByDescending(r => r.Changed)
                .Select(r => ItemInfoOf(key, r))
                .ToList();
        }
    }

    /// <summary>Saves a card or identity. Throws ArgumentException (see <see cref="ItemRules.Clean"/>) for fields that cannot be one.</summary>
    public Guid AddItem(string kind, IReadOnlyDictionary<string, string> fields)
    {
        var clean = ItemRules.Clean(kind, fields);

        lock (_gate)
        {
            var key = Key();
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            var row = new VaultItemRow
            {
                Id = id.ToString("N"),
                Kind = kind,
                Data = Sealed(key, id, kind, "data", JsonSerializer.Serialize(clean)),
                Created = now,
                Changed = now,
            };

            _file!.Items.Add(row);
            try
            {
                Write();
            }
            catch
            {
                _file.Items.Remove(row);
                throw;
            }

            return id;
        }
    }

    /// <summary>Replaces the fields of an item (a field left out is removed). False when there is no such item.</summary>
    public bool UpdateItem(Guid id, IReadOnlyDictionary<string, string> fields)
    {
        lock (_gate)
        {
            var key = Key();
            var old = FindItem(id);
            if (old is null)
                return false;

            var clean = ItemRules.Clean(old.Kind, fields);

            var next = old.Copy();
            next.Data = Sealed(key, id, old.Kind, "data", JsonSerializer.Serialize(clean));
            next.Changed = DateTimeOffset.UtcNow;

            var index = _file!.Items.IndexOf(old);
            _file.Items[index] = next;
            try
            {
                Write();
            }
            catch
            {
                _file.Items[index] = old;
                throw;
            }

            return true;
        }
    }

    public bool RemoveItem(Guid id)
    {
        lock (_gate)
        {
            Key();

            var row = FindItem(id);
            if (row is null)
                return false;

            var index = _file!.Items.IndexOf(row);
            _file.Items.RemoveAt(index);
            try
            {
                Write();
            }
            catch
            {
                _file.Items.Insert(index, row);
                throw;
            }

            return true;
        }
    }

    /// <summary>The fields of an item, for showing or editing it, only for someone who has just passed the gate.</summary>
    public Dictionary<string, string>? ReadItem(Guid id, Access access)
    {
        if (access is null || !access.Valid)
            throw new UnauthorizedAccessException("The sign-in has expired.");

        lock (_gate)
        {
            var key = Key();
            var row = FindItem(id);
            return row is null ? null : OpenItem(key, row);
        }
    }

    /// <summary>
    /// The fields of an item of the wanted kind, to be filled into the page the person is looking at when
    /// they pick it in the list (the same as <see cref="Fill"/> for a login: no sign-in, never shown).
    /// Null when there is no such item, it is of another kind, or it does not check out.
    /// </summary>
    public Dictionary<string, string>? FillItem(Guid id, string kind)
    {
        lock (_gate)
        {
            var key = Key();
            var row = FindItem(id);
            return row is null || row.Kind != kind ? null : OpenItem(key, row);
        }
    }

    private static Dictionary<string, string>? OpenItem(byte[] key, VaultItemRow row)
    {
        if (!Guid.TryParse(row.Id, out var id))
            return null;

        var blob = FromBase64(row.Data);
        var plain = blob is null ? null : Seal.Decrypt(key, blob, Aad.Field(id, row.Kind, "data"));
        if (plain is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            Seal.Wipe(plain);
        }
    }

    private static ItemInfo ItemInfoOf(byte[] key, VaultItemRow row)
    {
        var fields = OpenItem(key, row);
        var id = Guid.TryParse(row.Id, out var parsed) ? parsed : Guid.Empty;

        if (fields is null)
            return new ItemInfo(id, row.Kind, "(unreadable)", string.Empty, row.Created, row.Changed, true);

        return new ItemInfo(id, row.Kind, fields.GetValueOrDefault("label", row.Kind), ItemRules.Summary(row.Kind, fields), row.Created, row.Changed, false);
    }

    /// <summary>One item opened with <paramref name="old"/> and sealed again with <paramref name="fresh"/>. Null when it does not check out.</summary>
    private static VaultItemRow? ResealItem(VaultItemRow row, byte[] old, byte[] fresh)
    {
        if (!Guid.TryParse(row.Id, out var id))
            return null;

        var blob = FromBase64(row.Data);
        var plain = blob is null ? null : Seal.Decrypt(old, blob, Aad.Field(id, row.Kind, "data"));
        if (plain is null)
            return null;

        try
        {
            var again = row.Copy();
            again.Data = Sealed(fresh, id, row.Kind, "data", plain);
            return again;
        }
        finally
        {
            Seal.Wipe(plain);
        }
    }
}
