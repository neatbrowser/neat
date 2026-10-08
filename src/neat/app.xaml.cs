using Microsoft.UI.Xaml;

namespace neat;

public partial class App : Application
{
    private Window? _win;

    // Shared by every window. Set up once in OnLaunched, before the first window opens.
    public static Store Cfg { get; private set; } = null!;
    public static History Hist { get; private set; } = null!;
    public static Bookmarks Marks { get; private set; } = null!;
    public static Search Find { get; private set; } = null!;
    public static Look Look { get; private set; } = null!;

    public App()
    {
        // Must run before any WebView2 control is created, so the bundled
        // runtime and the profile folder are picked up.
        Env.Configure();

        // Anything that goes wrong without being caught is written to
        // crash.log before the process dies. None of these handlers marks the
        // error as handled, so the app behaves exactly as it did; they only
        // leave a trace.
        Log.Start();
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Error("domain", e.ExceptionObject as Exception, $"unhandled exception, terminating={e.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (s, e) =>
            Log.Error("task", e.Exception, "unobserved task exception");

        InitializeComponent();

        UnhandledException += (s, e) => Log.Error("xaml", e.Exception, e.Message);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Cfg = new Store(Path.Combine(Env.Root, "settings.json"));
        Cfg.Load();
        Find = new Search(Cfg);
        Look = new Look(Cfg);

        var db = new Db(Path.Combine(Env.Root, "browser.db"));
        Hist = new History(db);
        Marks = new Bookmarks(db);

        try
        {
            await Hist.Init();
            await Marks.Init();
        }
        catch (Exception ex)
        {
            // A broken database must not stop the browser from opening. The
            // window reports the problem where the history numbers would be.
            System.Diagnostics.Debug.WriteLine("[app] database init failed: " + ex.Message);
            Log.Error("db", ex, "database init failed");
        }

        _win = new Win();
        _win.Activate();
    }
}
