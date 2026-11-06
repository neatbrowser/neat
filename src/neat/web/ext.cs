namespace neat;

/// <summary>
/// One installed extension, as plain data. WebView2 only reports an id, a name and whether the
/// extension is on; anything else (version, icon, permissions) lives in the extension's own
/// manifest.json and is the business of the phase that owns the extension folders.
/// </summary>
public sealed class ExtItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>True when the extension is enabled.</summary>
    public bool On { get; init; }
}

/// <summary>
/// What happened to a change (add, enable, remove): either fine, or a sentence the UI can show.
/// </summary>
public sealed class ExtRes
{
    public bool Ok => Err is null;

    /// <summary>Why it failed, in English, ready to show; null when it worked.</summary>
    public string? Err { get; init; }

    /// <summary>The extension that was added; null for the other changes.</summary>
    public ExtItem? Item { get; init; }

    public static ExtRes Good(ExtItem? item = null) => new() { Item = item };

    public static ExtRes Bad(string why) => new() { Err = why };
}

/// <summary>
/// What <see cref="Ext"/> needs from a browser profile. The real one wraps a CoreWebView2Profile
/// (extprof.cs); keeping it behind this seam is what lets the wrappers be checked without a web
/// view (tests/ext.check).
/// </summary>
internal interface IExtProf
{
    Task<List<ExtItem>> All();

    /// <summary>Registers an unpacked extension from a folder and returns it.</summary>
    Task<ExtItem> Add(string folder);

    /// <summary>Turns an extension on or off; false when there is no extension with that id.</summary>
    Task<bool> Enable(string id, bool on);

    /// <summary>Removes an extension; false when there is no extension with that id.</summary>
    Task<bool> Remove(string id);
}

/// <summary>
/// Browser extensions in one place: whether they are available at all, and thin async wrappers
/// around the profile calls. Nothing here shows UI, and nothing throws: a failure comes back as an
/// empty list or an <see cref="ExtRes"/> with a message. Call from the UI thread, like the web
/// views the profile belongs to.
///
/// What WebView2 gives us is only part of what Chromium has. It loads the extension and runs its
/// pages, content scripts and background worker, but it supports only part of the extension API
/// and draws none of an extension's own UI (toolbar button, badge, popup): that is for the browser
/// to provide. Extensions are for the regular profile only, not for InPrivate.
/// </summary>
public static class Ext
{
    // Messages for the HRESULTs that WebView2 answers with when an extension call fails.
    private const int NotSupported = unchecked((int)0x80070032);
    private const int NotFound = unchecked((int)0x80070002);
    private const int NoPath = unchecked((int)0x80070003);
    private const int Denied = unchecked((int)0x80070005);
    private const int Busy = unchecked((int)0x8007139F);

    /// <summary>True when the web views were started with extensions on.</summary>
    public static bool Can { get; private set; }

    /// <summary>Why <see cref="Can"/> is false, as a sentence for the UI; empty when it is true.</summary>
    public static string Why { get; private set; } = "Extensions are not ready yet.";

    /// <summary>Gives the profile of a live tab, or null when none exists yet. Set by the window.</summary>
    internal static Func<IExtProf?>? Src { get; set; }

    internal static void Set(bool can, string? why)
    {
        Can = can;
        Why = can ? "" : why ?? "Extensions are not available.";
    }

    /// <summary>Every installed extension; empty when there are none, or when extensions are not available.</summary>
    public static async Task<List<ExtItem>> All()
    {
        var p = Prof();
        if (!Can || p is null)
            return new List<ExtItem>();

        try
        {
            return await p.All();
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "listing extensions failed");
            return new List<ExtItem>();
        }
    }

    /// <summary>
    /// Adds an extension from an UNPACKED folder (the one holding manifest.json). WebView2 does not
    /// copy the folder: it remembers where it is and drops the extension when the contents change,
    /// so the caller must keep the folder where it is.
    /// </summary>
    public static async Task<ExtRes> Add(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return ExtRes.Bad("No folder was given.");

        var p = Take(out var no);
        if (p is null)
            return no!;

        try
        {
            return ExtRes.Good(await p.Add(folder));
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "adding an extension failed");
            return ExtRes.Bad(Say(ex, "The extension could not be added."));
        }
    }

    /// <summary>Turns an extension on or off.</summary>
    public static async Task<ExtRes> Enable(string id, bool on)
    {
        var p = Take(out var no);
        if (p is null)
            return no!;

        try
        {
            return await p.Enable(id, on) ? ExtRes.Good() : ExtRes.Bad("That extension is not installed.");
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "switching an extension failed");
            return ExtRes.Bad(Say(ex, "The extension could not be switched."));
        }
    }

    /// <summary>Removes an extension from the profile. It does not touch the extension's folder.</summary>
    public static async Task<ExtRes> Remove(string id)
    {
        var p = Take(out var no);
        if (p is null)
            return no!;

        try
        {
            return await p.Remove(id) ? ExtRes.Good() : ExtRes.Bad("That extension is not installed.");
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "removing an extension failed");
            return ExtRes.Bad(Say(ex, "The extension could not be removed."));
        }
    }

    /// <summary>A sentence for an exception from an extension call; other is used when it has no better one.</summary>
    internal static string Say(Exception ex, string other) => ex.HResult switch
    {
        NotSupported => "Extensions are turned off in this copy of WebView2.",
        NotFound or NoPath => "That folder has no valid manifest.json.",
        Denied => "The folder could not be read. A file or folder whose name starts with an underscore is not allowed.",
        Busy => "This profile is in use by a copy of NEAT that runs without extensions.",
        _ => other,
    };

    // Asked for again on every call and never kept: a profile belongs to the web view it came from
    // and stops working when that tab closes, while the other tabs (same profile) are fine.
    private static IExtProf? Prof()
    {
        try
        {
            return Src?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "no usable profile");
            return null;
        }
    }

    // The profile a change goes to, taken once so it cannot vanish between the check and the use.
    // When there is none, no says why the change cannot even be tried.
    private static IExtProf? Take(out ExtRes? no)
    {
        no = null;
        if (!Can)
        {
            no = ExtRes.Bad(Why);
            return null;
        }

        var p = Prof();
        if (p is null)
            no = ExtRes.Bad("The browser is still starting. Try again in a moment.");

        return p;
    }
}
