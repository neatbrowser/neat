namespace neat;

/// <summary>
/// A login a page showed NEAT, waiting to find out whether it worked.
/// </summary>
public sealed record Pending(string Site, string User, string Password, DateTime At);

/// <summary>
/// Remembers, for each tab, the login the person has just submitted, until it is known whether
/// the login worked. It decides nothing about the screen: the window feeds it what the page
/// said and what happened next, and asks what to offer.
///
/// The page's script reports a login when the person presses a button or Enter (a real key or
/// click, never one the page made up). That is not yet a reason to offer to save it: the
/// password may be wrong, or the click may only have been the "show password" eye. So the
/// login is held here for <see cref="Window"/> and the window offers it only when the page
/// moved on (the next page has no password field, or the field went away). A failed login
/// shows the form again and the held login is dropped.
///
/// Many sites (Google, Microsoft) ask for the user name on one page and the password on the next,
/// and the password page has no user name field to read. So the page script also reports the user
/// name from the first step (see <see cref="HoldUser"/>), and it is filled in when a login arrives
/// from the same site without one.
///
/// A held password or user name lives in memory only, for that short time, and is dropped as soon
/// as it is taken. Nothing here is written anywhere.
/// </summary>
public sealed class Capture
{
    /// <summary>How long a login is held while waiting to find out how it went.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    /// <summary>After offering to save for a site, it is not offered again for this long.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(20);

    /// <summary>How long the user name of a first step is remembered, waiting for the password step.</summary>
    public static readonly TimeSpan UserWindow = TimeSpan.FromMinutes(3);

    /// <summary>The longest user name or password accepted: anything longer is not a login.</summary>
    public const int MaxLength = 512;

    private readonly Dictionary<object, Pending> _held = new();
    private readonly Dictionary<object, (string Site, string User, DateTime At)> _users = new();
    private readonly Dictionary<string, DateTime> _offered = new(StringComparer.Ordinal);

    // The clock this (and the self-check) uses.
    internal Func<DateTime> Clock = () => DateTime.UtcNow;

    /// <summary>
    /// Holds a login for a tab, replacing any it held. False (and nothing held) when it is not
    /// worth holding: not a web origin, no password, or longer than <see cref="MaxLength"/>.
    /// </summary>
    public bool Hold(object tab, string site, string user, string password)
    {
        var origin = Origin.Of(site);

        if (origin is null || password.Length == 0 || password.Length > MaxLength || user.Length > MaxLength)
            return false;

        // A password page with no user name of its own: use the one from the step before, if it was
        // on the same site and not long ago. A user name that came with the login is never replaced.
        if (user.Length == 0 && _users.TryGetValue(tab, out var known)
            && known.Site == origin && Clock() - known.At < UserWindow)
        {
            user = known.User;
        }

        _held[tab] = new Pending(origin, user, password, Clock());
        return true;
    }

    /// <summary>
    /// Remembers the user name typed on the first step of a two-step login, for the password step.
    /// False (nothing kept) for a user name that is empty, too long, or on a page that is not a web origin.
    /// </summary>
    public bool HoldUser(object tab, string site, string user)
    {
        var origin = Origin.Of(site);

        if (origin is null || user.Length == 0 || user.Length > MaxLength)
            return false;

        _users[tab] = (origin, user, Clock());
        return true;
    }

    public bool Holds(object tab)
    {
        return _held.TryGetValue(tab, out var p) && Fresh(p);
    }

    /// <summary>
    /// Takes the held login out, for offering it. Null when there is none or it waited too long.
    /// Either way nothing is held for the tab afterwards.
    /// </summary>
    public Pending? Take(object tab)
    {
        if (!_held.Remove(tab, out var p))
            return null;

        // The login has got as far as being offered: the user name of the step before is spent.
        _users.Remove(tab);

        return Fresh(p) ? p : null;
    }

    /// <summary>
    /// Forgets the login held for a tab because it failed. The user name of the first step is kept,
    /// because the person is about to try the password again.
    /// </summary>
    public void Drop(object tab)
    {
        _held.Remove(tab);
    }

    /// <summary>Forgets everything held for a tab that has been closed.</summary>
    public void Forget(object tab)
    {
        _held.Remove(tab);
        _users.Remove(tab);
    }

    /// <summary>Forgets everything held.</summary>
    public void Clear()
    {
        _held.Clear();
        _users.Clear();
    }

    /// <summary>
    /// Whether an offer for this site may be shown now, and if so notes that it was. A page that
    /// keeps provoking offers gets one every <see cref="Cooldown"/>, not one every time.
    /// </summary>
    public bool MayOffer(string site)
    {
        var now = Clock();

        if (_offered.TryGetValue(site, out var last) && now - last < Cooldown)
            return false;

        _offered[site] = now;

        // Old notes are not kept.
        foreach (var old in _offered.Where(kv => now - kv.Value >= Cooldown).Select(kv => kv.Key).ToList())
            _offered.Remove(old);

        return true;
    }

    private bool Fresh(Pending p)
    {
        return Clock() - p.At < Window;
    }
}
