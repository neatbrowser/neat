namespace neat;

/// <summary>One search engine the address box can use.</summary>
public sealed class Engine
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Address template; %s stands for the URL-encoded query.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Short prefix for a one-off search: "!b cats" uses the engine whose key is "b".</summary>
    public string Key { get; set; } = string.Empty;

    public static List<Engine> Defaults() => new()
    {
        new Engine { Name = "Google",     Url = "https://www.google.com/search?q=%s", Key = "g" },
        new Engine { Name = "Bing",       Url = "https://www.bing.com/search?q=%s",   Key = "b" },
        new Engine { Name = "DuckDuckGo", Url = "https://duckduckgo.com/?q=%s",       Key = "d" },
        new Engine { Name = "Cốc Cốc",    Url = "https://coccoc.com/search?query=%s", Key = "c" },
    };
}

/// <summary>Where the window was last time, so it opens the same way.</summary>
public sealed class Geo
{
    public int W { get; set; } = 1280;
    public int H { get; set; } = 800;
    public bool Max { get; set; }
}

/// <summary>
/// Everything saved in settings.json. A plain data object with no logic, so it
/// round-trips cleanly through System.Text.Json. Unknown keys in the file are
/// ignored, so new settings can be added in later phases without breaking old files.
/// </summary>
public sealed class Prefs
{
    /// <summary>Page the first tab opens on at startup.</summary>
    public string Home { get; set; } = "https://www.google.com";

    /// <summary>Name of the engine in use. Must match one of Engines.</summary>
    public string Use { get; set; } = "Google";

    public List<Engine> Engines { get; set; } = Engine.Defaults();

    public Geo Geo { get; set; } = new();
}
