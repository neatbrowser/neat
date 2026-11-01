namespace neat;

/// <summary>A card a page showed NEAT, waiting to find out whether the payment form went through.</summary>
public sealed record PendingCard(string Site, string Number, string Holder, string Expiry, string Cvc, DateTime At);

/// <summary>
/// Remembers, for each tab, the card the person has just submitted in a payment form, until it is known
/// whether the form went through. It is <see cref="Capture"/> for cards, and keeps the same rules:
///   - The page script reports a card only for a real click or key (never one the page made up), and
///     only a number that passes the check digit (<see cref="ItemRules.Luhn"/>).
///   - Reporting is not asking. The window offers to save only when the page moved on (the form went
///     away), and a server that refuses a request of the same site (see <see cref="LoginNet"/>) drops it.
///   - It lives in memory for <see cref="Window"/> and is dropped as soon as it is taken. Nothing is written.
/// </summary>
public sealed class CardCapture
{
    /// <summary>How long a card is held while waiting to find out how it went.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    /// <summary>After offering to save a card for a site, it is not offered again for this long.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly Dictionary<object, PendingCard> _held = new();
    private readonly Dictionary<string, DateTime> _offered = new(StringComparer.Ordinal);

    // The clock this (and the self-check) uses.
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>
    /// Holds a card for a tab, replacing any it held. False (nothing held) when it is not a web origin or
    /// the number is not a card number. A security code or expiry that is not valid is left out, not refused.
    /// </summary>
    public bool Hold(object tab, string site, string number, string holder, string expiry, string cvc)
    {
        var origin = Origin.Of(site);
        var digits = ItemRules.Digits(number);

        if (origin is null || !ItemRules.Luhn(digits) || digits.Length != number.Count(c => char.IsAsciiDigit(c)))
            return false;

        holder = holder.Trim();
        if (holder.Length > 100)
            holder = string.Empty;

        cvc = cvc.Trim();
        if (cvc.Length is < 3 or > 4 || !cvc.All(char.IsAsciiDigit))
            cvc = string.Empty;

        _held[tab] = new PendingCard(origin, digits, holder, ItemRules.Expiry(expiry) ?? string.Empty, cvc, Clock());
        return true;
    }

    public bool Holds(object tab)
    {
        return _held.TryGetValue(tab, out var p) && Fresh(p);
    }

    /// <summary>Takes the held card out, for offering it. Null when there is none or it waited too long. Nothing is held afterwards.</summary>
    public PendingCard? Take(object tab)
    {
        return _held.Remove(tab, out var p) && Fresh(p) ? p : null;
    }

    public void Drop(object tab)
    {
        _held.Remove(tab);
    }

    /// <summary>Told about an answer the tab's page received: a refusal of the same site drops the held card. True when it did.</summary>
    public bool Answered(object tab, string? method, string? url, int status)
    {
        if (!_held.TryGetValue(tab, out var p) || !Fresh(p) || !LoginNet.Refuses(p.Site, method, url, status))
            return false;

        Drop(tab);
        return true;
    }

    public void Forget(object tab)
    {
        _held.Remove(tab);
    }

    public void Clear()
    {
        _held.Clear();
    }

    /// <summary>Whether an offer for this site may be shown now, and if so notes that it was.</summary>
    public bool MayOffer(string site)
    {
        var now = Clock();

        if (_offered.TryGetValue(site, out var last) && now - last < Cooldown)
            return false;

        _offered[site] = now;

        foreach (var old in _offered.Where(kv => now - kv.Value >= Cooldown).Select(kv => kv.Key).ToList())
            _offered.Remove(old);

        return true;
    }

    private bool Fresh(PendingCard p)
    {
        return Clock() - p.At < Window;
    }
}
