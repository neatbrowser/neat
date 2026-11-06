using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Tells WebView2 where to find its runtime and where to keep the browsing
/// profile. Call <see cref="Configure"/> once at startup, before any WebView2
/// control exists.
///
/// Release builds ship a Fixed Version WebView2 runtime in a "WebView2" folder
/// next to neat.exe (copied there by the WebView2.Runtime.X64 NuGet package).
/// When that folder exists we use it, so the app never depends on the
/// system-wide Evergreen runtime. When it does not exist, WebView2 falls back
/// to whatever Evergreen runtime is installed (a dev-time safety net).
///
/// The paths go through WebView2's documented environment variables, which
/// WebView2 also lets win over the values passed to it in code. On top of that
/// the app makes ONE explicit CoreWebView2Environment (<see cref="Get"/>) that
/// every tab is started on (<see cref="Start"/>). It exists because browser
/// extensions can only be switched on through the environment's options, and
/// every web view that shares a profile must use identical options.
/// </summary>
internal static class Env
{
    /// <summary>Folder holding the bundled runtime, or null when not bundled.</summary>
    public static string? Fixed { get; private set; }

    /// <summary>Per-user folder for everything the app saves (settings, database, profile).</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEAT");

    /// <summary>Folder where WebView2 keeps cookies, cache and other profile data.</summary>
    public static string Data { get; private set; } = string.Empty;

    public static bool UsesFixed => Fixed is not null;

    /// <summary>
    /// Adds arguments for the browser engine behind every web view (WebView2 reads them from this
    /// environment variable). Call before the first web view exists.
    /// </summary>
    public static void AddBrowserArguments(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return;

        const string name = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
        var now = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, string.IsNullOrWhiteSpace(now) ? args : now + " " + args);
    }

    public static void Configure()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "WebView2");
        if (Directory.Exists(dir))
        {
            Fixed = dir;
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", dir);
        }

        // Per-user and always writable, even when the app is installed under
        // Program Files (WebView2's default would be next to the .exe).
        Data = Path.Combine(Root, "WebView2Profile");

        Directory.CreateDirectory(Data);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Data);
    }

    // HRESULT_FROM_WIN32(ERROR_INVALID_STATE). WebView2 answers with it when this profile already
    // has a running browser process whose environment options differ from ours.
    private const int Busy = unchecked((int)0x8007139F);

    // The one environment, made on first use. Only touched on the UI thread.
    private static Task<CoreWebView2Environment?>? _env;

    /// <summary>
    /// The environment every web view is started on, made once and then shared. Starts the work
    /// on the first call (App does that early so it is ready before the first tab) and never
    /// throws: when no environment can be made the result is null and <see cref="Start"/> falls
    /// back to what the variables from <see cref="Configure"/> give, which is how the browser
    /// worked before this environment existed.
    /// </summary>
    public static Task<CoreWebView2Environment?> Get() => _env ??= Make();

    /// <summary>
    /// Starts a tab's web view on the shared environment. Every web view the app creates by itself
    /// goes through here; a popup that a page opens is started on its opener's environment instead
    /// (win.popup.cs), which is the same one.
    /// </summary>
    public static async Task Start(Microsoft.UI.Xaml.Controls.WebView2 view)
    {
        var env = await Get();
        if (env is null)
        {
            await view.EnsureCoreWebView2Async();
            return;
        }

        try
        {
            await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (Ext.Can && ex.HResult == Busy)
        {
            // Another NEAT (an older one, without extensions) is still running on this profile and
            // its options do not match ours. Join it the way it runs, with extensions off, rather
            // than leave the user without a browser.
            Log.Error("webview", ex, "profile is in use with other options, starting without extensions");
            Ext.Set(false, "This profile is in use by a copy of NEAT that runs without extensions. Close it and start NEAT again.");

            var plain = await Plain();
            _env = Task.FromResult(plain);
            if (plain is null)
                await view.EnsureCoreWebView2Async();
            else
                await view.EnsureCoreWebView2Async(plain);
        }
    }

    private static async Task<CoreWebView2Environment?> Make()
    {
        try
        {
            // Extensions are decided when the environment is made and cannot be switched on later,
            // so this is the one place that asks for them. The runtime folder, the profile folder
            // and the extra browser arguments still come from Configure and AddBrowserArguments
            // (WebView2 reads those variables and lets them win over what is passed here), so
            // nothing else about the browser changes: same profile, same cookies, same flags.
            var opt = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(Fixed, Data, opt);
            Ext.Set(true, null);
            Log.Write("webview", "environment ready, extensions on");
            return env;
        }
        catch (Exception ex)
        {
            // An older runtime, a refused option, a folder problem: the browser must open anyway.
            Log.Error("webview", ex, "environment with extensions failed, trying without");
            Ext.Set(false, Ext.Say(ex, "Extensions could not be started in this copy of WebView2."));
        }

        return await Plain();
    }

    private static async Task<CoreWebView2Environment?> Plain()
    {
        try
        {
            return await CoreWebView2Environment.CreateWithOptionsAsync(Fixed, Data, new CoreWebView2EnvironmentOptions());
        }
        catch (Exception ex)
        {
            Log.Error("webview", ex, "environment without extensions failed, using the default one");
            return null;
        }
    }
}
