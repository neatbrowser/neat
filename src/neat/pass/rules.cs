using System.Text.Json;

namespace neat;

/// <summary>
/// Rules for single sites, read from pass/rules.json (embedded in neat.exe) and handed to the page
/// script of the save-password bubble (pass/capture.cs). They are for the pages where the script's
/// own guess about the login form is wrong. A rule only helps the script find or leave out fields; it
/// never fills or saves anything by itself, and the rest of the checks still apply.
///
/// The file is { "version": 1, "rules": { "<host>[/<path>]": { ... } } }. A key is a host, which also
/// covers its subdomains ("example.com" covers "www.example.com", but not "notexample.com"), with an
/// optional path that the page's path must start with ("example.com/login"). A rule has either part:
///
///   "exclude": ["css selector", ...]
///       Fields inside anything these match are ignored, as if they were not on the page.
///
///   "login": [ { "user": PATH, "pass": PATH }, ... ]
///       The exact fields of a login form. "pass" is needed, "user" is optional, and the user name field
///       may be hidden. The first entry whose password field is on show and filled is the login.
///
/// A PATH is a selector, or a list of selectors where each one is looked for inside the one before it:
/// in its own tree first, then in its shadow root. So ["#host", "input[name=pw]"] finds an input that
/// lives in the shadow root of #host.
///
/// Anything that cannot be read leaves the script with no rules at all ("{}"), which is how it ran
/// before there were any. The script ignores entries it cannot make sense of one by one.
/// </summary>
internal static class SiteRules
{
    // A file bigger than this is not believed: the text goes into every page the person opens.
    private const int MaxChars = 256 * 1024;

    private static string? _json;

    /// <summary>The "rules" object as JSON text, ready to be put in the script. "{}" when there are none.</summary>
    public static string Json => _json ??= Load();

    private static string Load()
    {
        try
        {
            using var s = typeof(SiteRules).Assembly.GetManifestResourceStream("neat.pass.rules.json");
            if (s is null)
            {
                Log.Write("pass", "site rules are not embedded");
                return "{}";
            }

            using var r = new StreamReader(s);
            return Extract(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Error("pass", ex, "the site rules could not be read");
            return "{}";
        }
    }

    /// <summary>The "rules" object of a rules file as compact JSON, or "{}" if the text is not a rules file.</summary>
    internal static string Extract(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("rules", out var rules)
                || rules.ValueKind != JsonValueKind.Object)
                return "{}";

            // Written out again, so comments are gone and every non-ASCII character is escaped.
            var json = JsonSerializer.Serialize(rules);
            return json.Length <= MaxChars ? json : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
