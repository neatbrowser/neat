namespace neat;

/// <summary>
/// One login on its way into or out of a CSV file. <paramref name="Otp"/> is the key of its one-time codes in the
/// canonical form (see <see cref="Totp"/>), or null.
/// </summary>
public sealed record ImportRow(string Site, string User, string Password, string? Otp = null);

/// <summary>One saved card or identity on its way into or out of a CSV file: its kind and its fields (label included).</summary>
public sealed record ItemRow(string Kind, Dictionary<string, string> Fields);

/// <summary>What was read from a file of cards and identities: the items that can be imported, how many rows could not be, and why not all.</summary>
public sealed record ReadItemsResult(List<ItemRow> Rows, int Skipped, string? Problem);

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

    // Bitwarden calls the column login_totp, Proton Pass totp, 1Password otpauth.
    private static readonly string[] OtpNames = { "totp", "login_totp", "otpauth", "otp" };

    /// <summary>The columns of a file of cards and identities: what it is, then every field of either kind.</summary>
    private static readonly string[] ItemColumns =
    {
        "type", "label", "holder", "number", "expiry", "cvc",
        "first", "last", "email", "phone", "org", "street", "street2", "city", "state", "zip", "country",
    };

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
        var otp = Column(header, OtpNames);

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

            // A key that is not one is left out; the login itself is still imported.
            var key = otp < 0 ? null : Totp.Parse(Cell(r, otp))?.Canonical();

            rows.Add(new ImportRow(site, name, password, key));
        }

        return new ReadResult(rows, skipped, null);
    }

    /// <summary>
    /// The logins as a CSV text in the column order Chrome uses (name, url, username, password, note), with a
    /// totp column after them only when some login has a one-time-code key (a program that does not know
    /// the column skips it).
    /// </summary>
    public static string Write(IEnumerable<ImportRow> rows)
    {
        var list = rows as IReadOnlyCollection<ImportRow> ?? rows.ToList();
        var keys = list.Any(r => r.Otp is not null);

        var lines = new List<string> { keys ? Csv.Row("name", "url", "username", "password", "note", "totp") : Csv.Row("name", "url", "username", "password", "note") };

        foreach (var r in list)
        {
            var host = Uri.TryCreate(r.Site, UriKind.Absolute, out var u) ? u.Host : r.Site;
            lines.Add(keys
                ? Csv.Row(host, r.Site, r.User, r.Password, string.Empty, r.Otp ?? string.Empty)
                : Csv.Row(host, r.Site, r.User, r.Password, string.Empty));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Whether a file is one of cards and identities (as <see cref="WriteItems"/> makes it) and not of logins.</summary>
    public static bool LooksLikeItems(string text)
    {
        var table = Csv.Parse(text);
        if (table.Count == 0)
            return false;

        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        return header.Contains("type") && Column(header, UrlNames) < 0 && Column(header, PassNames) < 0;
    }

    /// <summary>
    /// Reads a file of cards and identities by the names in its first row. A row of another type, or one
    /// that is not a card or identity (a number that is not a number, say), is skipped and counted.
    /// </summary>
    public static ReadItemsResult ReadItems(string text)
    {
        var table = Csv.Parse(text);
        var none = new List<ItemRow>();

        if (table.Count == 0)
            return new ReadItemsResult(none, 0, "The file is empty.");

        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var type = header.IndexOf("type");
        if (type < 0 || Column(header, UrlNames) >= 0)
            return new ReadItemsResult(none, 0, "This does not look like a file of cards and identities: it has no type column.");

        if (table.Count - 1 > MaxRows)
            return new ReadItemsResult(none, 0, "The file has more than " + MaxRows + " rows, which is more than NEAT will import at once.");

        var rows = new List<ItemRow>();
        var skipped = 0;

        foreach (var r in table.Skip(1))
        {
            var kind = Cell(r, type).Trim().ToLowerInvariant();
            var known = ItemRules.FieldsOf(kind);
            if (known is null)
            {
                skipped++;
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in known)
            {
                var at = header.IndexOf(name);
                if (at >= 0 && Cell(r, at).Trim().Length > 0)
                    fields[name] = Cell(r, at);
            }

            try
            {
                rows.Add(new ItemRow(kind, ItemRules.Clean(kind, fields)));
            }
            catch (ArgumentException)
            {
                skipped++;
            }
        }

        return new ReadItemsResult(rows, skipped, null);
    }

    /// <summary>Cards and identities as one CSV text: a type column, then every field of either kind (blank where it does not apply).</summary>
    public static string WriteItems(IEnumerable<ItemRow> rows)
    {
        var lines = new List<string> { Csv.Row(ItemColumns) };

        foreach (var r in rows)
        {
            var cells = ItemColumns
                .Select(c => c == "type" ? r.Kind : r.Fields.GetValueOrDefault(c, string.Empty))
                .ToArray();

            lines.Add(Csv.Row(cells));
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
