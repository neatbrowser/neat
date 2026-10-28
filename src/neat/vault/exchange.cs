namespace neat;

/// <summary>One login on its way into or out of a CSV file.</summary>
public sealed record ImportRow(string Site, string User, string Password);

/// <summary>What was read from a CSV file: the logins that can be imported, how many rows could not be, and why not all.</summary>
public sealed record ReadResult(List<ImportRow> Rows, int Skipped, string? Problem);

/// <summary>
/// Turns a password export from another browser into logins, and the vault's logins into the same
/// kind of file. The file is plain text, so the callers say so loudly; nothing here keeps it.
/// </summary>
public static class Exchange
{
    /// <summary>The most rows read from one file.</summary>
    public const int MaxRows = 10000;

    private static readonly string[] UrlNames = { "url", "login_uri", "origin", "website", "web site" };
    private static readonly string[] UserNames = { "username", "login_username", "user name", "user", "login" };
    private static readonly string[] PassNames = { "password", "login_password" };

    /// <summary>
    /// Reads a CSV export by the names in its first row, so the files of Chrome, Edge and Firefox
    /// (which order their columns differently) all work. Rows that are not logins for a web
    /// site (an app, a browser page), have no password, or are too long are skipped and counted.
    /// </summary>
    public static ReadResult Read(string text)
    {
        var table = Csv.Parse(text);
        var none = new List<ImportRow>();

        if (table.Count == 0)
            return new ReadResult(none, 0, "The file is empty.");

        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var url = Column(header, UrlNames);
        var user = Column(header, UserNames);
        var pass = Column(header, PassNames);

        if (url < 0 || pass < 0)
            return new ReadResult(none, 0, "This does not look like a password export: it has no url and password columns.");

        if (table.Count - 1 > MaxRows)
            return new ReadResult(none, 0, "The file has more than " + MaxRows + " rows, which is more than NEAT will import at once.");

        var rows = new List<ImportRow>();
        var skipped = 0;

        foreach (var r in table.Skip(1))
        {
            var site = Origin.Of(Cell(r, url));
            var name = user < 0 ? string.Empty : Cell(r, user).Trim();
            var password = Cell(r, pass);

            if (site is null || password.Length == 0 || password.Length > Capture.MaxLength || name.Length > Capture.MaxLength)
            {
                skipped++;
                continue;
            }

            rows.Add(new ImportRow(site, name, password));
        }

        return new ReadResult(rows, skipped, null);
    }

    /// <summary>The logins as a CSV text in the column order Chrome uses (name, url, username, password, note).</summary>
    public static string Write(IEnumerable<ImportRow> rows)
    {
        var lines = new List<string> { Csv.Row("name", "url", "username", "password", "note") };

        foreach (var r in rows)
        {
            var host = Uri.TryCreate(r.Site, UriKind.Absolute, out var u) ? u.Host : r.Site;
            lines.Add(Csv.Row(host, r.Site, r.User, r.Password, string.Empty));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    private static int Column(List<string> header, string[] names)
    {
        foreach (var name in names)
        {
            var at = header.IndexOf(name);
            if (at >= 0)
                return at;
        }

        return -1;
    }

    private static string Cell(string[] row, int at)
    {
        return at >= 0 && at < row.Length ? row[at] : string.Empty;
    }
}
