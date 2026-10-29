using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace neat;

/// <summary>
/// The fingerprint protection's plain-code part: which level a site gets, what the page script
/// is filled in with, what a request's headers become. No WinUI in here, so the self-check
/// (tests/fp.check) can run it. The window's part is win.fp.cs; the script is fp/farble.js.
/// </summary>
internal static class Fp
{
    /// <summary>Raised (on the UI thread) after the settings changed; every window then registers its page script again.</summary>
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();

    private static string? _secret;

    /// <summary>
    /// Made when the browser starts and never saved: it is what makes the values a page sees differ
    /// after a restart. The page script holds it in a closure; a page cannot read it.
    /// </summary>
    public static string Secret => _secret ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string? _template;

    /// <summary>The text of fp/farble.js, read once.</summary>
    public static string Template => _template ??= ReadTemplate();

    private static string ReadTemplate()
    {
        using var s = typeof(Fp).Assembly.GetManifestResourceStream("neat.fp.farble.js")
            ?? throw new InvalidOperationException("fp/farble.js is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>
    /// The level for a page on this host: the most specific entry of the site list that covers it,
    /// else the default; 0 when the protection is off or the host is unknown.
    /// </summary>
    public static int LevelFor(FpPrefs p, string? host)
    {
        if (!p.On || host is null)
            return FpLevel.Off;

        var level = FpLevel.Clamp(p.Default);
        var best = -1;
        foreach (var (k, v) in p.Sites)
        {
            if (k.Length > best && AdUrl.Covers(k, host))
            {
                best = k.Length;
                level = FpLevel.Clamp(v);
            }
        }

        return level;
    }

    /// <summary>
    /// Sets the level for one site: every entry that covers the host goes, and the site is listed
    /// (without "www.", so it covers its other subdomains) only when the level differs from the default.
    /// A new dictionary is stored, never an edit of the old one.
    /// </summary>
    public static void SetLevel(FpPrefs p, string host, int level)
    {
        level = FpLevel.Clamp(level);
        var list = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in p.Sites)
        {
            if (!AdUrl.Covers(k, host))
                list[k] = v;
        }

        if (level != FpLevel.Clamp(p.Default))
            list[host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host] = level;

        p.Sites = list;
    }

    /// <summary>True when any page could be protected, so a page script is worth registering at all.</summary>
    public static bool Needed(FpPrefs p) =>
        p.On && (FpLevel.Clamp(p.Default) > FpLevel.Off || p.Sites.Values.Any(v => v > FpLevel.Off));

    /// <summary>The page script for the current settings (fp/farble.js with its settings filled in).</summary>
    public static string Build(string template, string secret, FpPrefs p)
    {
        var sites = new JsonArray();
        var def = 0;
        if (p.On)
        {
            def = FpLevel.Clamp(p.Default);
            foreach (var (k, v) in p.Sites)
                sites.Add(new JsonArray(k, FpLevel.Clamp(v)));
        }

        var cfg = new JsonObject
        {
            ["secret"] = secret,
            ["def"] = def,
            ["sites"] = sites,
            ["gpc"] = p.Gpc,
        };

        // The filling-in goes last and into a text with a single place for it.
        return template.Replace("__NEAT_FP__", cfg.ToJsonString());
    }

    /// <summary>
    /// The Accept-Language header a protected page's requests carry, or null to leave it alone.
    /// Standard keeps only the first language, as navigator.languages then does; strict says English.
    /// </summary>
    public static string? AcceptLanguage(int level, string? original)
    {
        if (level <= FpLevel.Off)
            return null;

        if (level >= FpLevel.Strict)
            return "en-US,en;q=0.9";

        if (string.IsNullOrWhiteSpace(original))
            return null;

        var first = original.Split(',')[0].Split(';')[0].Trim();
        return first.Length == 0 ? null : first;
    }

    /// <summary>The browser arguments the WebRTC setting asks for, or empty.</summary>
    public static string BrowserArguments(FpPrefs p) =>
        p.On && p.WebRtc ? "--force-webrtc-ip-handling-policy=default_public_interface_only" : string.Empty;
}
