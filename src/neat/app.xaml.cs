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

    public App()
    {
        // Must run before any WebView2 control is created, so the bundled
        // runtime and the profile folder are picked up.
        Env.Configure();

        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Cfg = new Store(Path.Combine(Env.Root, "settings.json"));
        Cfg.Load();
        Find = new Search(Cfg);

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
        }

        _win = new Win();
        _win.Activate();
    }
}
