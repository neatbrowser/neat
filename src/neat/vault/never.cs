namespace neat;

/// <summary>
/// The sites the person said never to offer to save a password for ("Never for this site" on the
/// save bubble). It is a plain list of origins kept in settings.json, not in the vault: it must
/// work while a password vault is shut, and it holds no secret, only which sites were declined.
/// </summary>
public static class Never
{
    /// <summary>The most sites kept.</summary>
    public const int Max = 5000;

    public static bool Has(IEnumerable<string> list, string origin)
    {
        return list.Contains(origin, StringComparer.Ordinal);
    }

    /// <summary>Adds an origin. False when it is not an origin, already there, or the list is full.</summary>
    public static bool Add(List<string> list, string origin)
    {
        if (Origin.Of(origin) != origin || Has(list, origin) || list.Count >= Max)
            return false;

        list.Add(origin);
        return true;
    }

    /// <summary>Takes an origin off the list. False when it was not there.</summary>
    public static bool Remove(List<string> list, string origin)
    {
        return list.RemoveAll(o => string.Equals(o, origin, StringComparison.Ordinal)) > 0;
    }

    /// <summary>A list read from disk, put in order: only real origins, written one way, once each.</summary>
    public static List<string> Clean(IEnumerable<string>? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clean = new List<string>();

        foreach (var item in raw ?? Enumerable.Empty<string>())
        {
            var origin = Origin.Of(item);
            if (origin is not null && seen.Add(origin) && clean.Count < Max)
                clean.Add(origin);
        }

        return clean;
    }
}
