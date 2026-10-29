namespace neat;

/// <summary>
/// How strongly a page is protected against browser fingerprinting. The numbers are what is saved
/// in settings.json and what fp/farble.js reads, so they never change.
/// </summary>
internal static class FpLevel
{
    public const int Off = 0;

    /// <summary>Values are changed by a little or lose detail; pages keep working.</summary>
    public const int Standard = 1;

    /// <summary>Goes further (noise for audio, random graphics-card names, a fixed language list); some pages may break.</summary>
    public const int Strict = 2;

    public static int Clamp(int level) => Math.Clamp(level, Off, Strict);

    public static string Name(int level) => Clamp(level) switch
    {
        Off => "Off",
        Standard => "Standard",
        _ => "Strict",
    };
}

/// <summary>
/// Settings of the fingerprint protection (fp/). A plain data object saved in settings.json
/// (Prefs.Fp). The window re-registers its page script when these change, so changes apply to the
/// next page load without a restart; only <see cref="WebRtc"/> is read once at start.
/// </summary>
public sealed class FpPrefs
{
    /// <summary>False turns the protection off everywhere.</summary>
    public bool On { get; set; } = true;

    /// <summary>Level for every site that has no entry in <see cref="Sites"/>: 0 off, 1 standard, 2 strict.</summary>
    public int Default { get; set; } = FpLevel.Standard;

    /// <summary>
    /// Levels chosen for single sites (a host like "example.com", which also covers its subdomains).
    /// The most specific entry wins. Replaced as a whole when it changes, never edited in place, so
    /// a request on another thread can read it.
    /// </summary>
    public Dictionary<string, int> Sites { get; set; } = new();

    /// <summary>
    /// True sends "Sec-GPC: 1" (the "do not sell or share my data" signal) and shows it to scripts as
    /// navigator.globalPrivacyControl, on every page that is protected.
    /// </summary>
    public bool Gpc { get; set; } = true;

    /// <summary>
    /// True keeps web pages from learning the computer's addresses through WebRTC: it only uses the
    /// network route ordinary traffic takes (Chromium's default_public_interface_only). Read once
    /// at start, so it applies after a restart.
    /// </summary>
    public bool WebRtc { get; set; } = true;

    /// <summary>Most single-site entries kept.</summary>
    public const int MaxSites = 2000;

    /// <summary>Puts a loaded settings file in order: levels in range, hosts written one way, nothing missing.</summary>
    public void Clean()
    {
        Default = FpLevel.Clamp(Default);

        var clean = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, level) in Sites ?? new Dictionary<string, int>())
        {
            var h = (host ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
            if (h.Length is 0 or > 253 || !h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
                continue;

            if (clean.Count >= MaxSites)
                break;

            clean[h] = FpLevel.Clamp(level);
        }

        Sites = clean;
    }
}
