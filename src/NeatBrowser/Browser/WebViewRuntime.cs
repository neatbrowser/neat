namespace NeatBrowser.Browser;

/// <summary>
/// Tells WebView2 where to find its runtime and where to keep the browsing
/// profile. Call <see cref="Configure"/> once at startup, before any WebView2
/// control exists.
///
/// RUNTIME: release builds ship a Fixed Version WebView2 runtime in a
/// "WebView2" folder next to NeatBrowser.exe (copied there by the
/// WebView2.Runtime.X64 NuGet package). When that folder exists we use it, so
/// the app never depends on the system-wide Evergreen runtime. When it does
/// not exist, WebView2 falls back to whatever Evergreen runtime is installed
/// (a dev-time safety net).
///
/// HOW: for this first phase the paths are passed through WebView2's
/// documented environment variables, which every WebView2 host honours. That
/// avoids guessing API signatures. A later phase will create a proper
/// CoreWebView2Environment (needed for extensions and the custom neat://
/// scheme) and replace this.
/// </summary>
internal static class WebViewRuntime
{
    /// <summary>Folder holding the bundled runtime, or null when not bundled.</summary>
    public static string? FixedRuntimeFolder { get; private set; }

    /// <summary>Folder where WebView2 keeps cookies, cache and other profile data.</summary>
    public static string UserDataFolder { get; private set; } = string.Empty;

    public static bool IsUsingFixedRuntime => FixedRuntimeFolder is not null;

    public static void Configure()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "WebView2");
        if (Directory.Exists(bundled))
        {
            FixedRuntimeFolder = bundled;
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", bundled);
        }

        // Per-user and always writable, even when the app is installed under
        // Program Files (WebView2's default would be next to the .exe).
        UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEAT",
            "WebView2Profile");

        Directory.CreateDirectory(UserDataFolder);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", UserDataFolder);
    }
}
