using Microsoft.UI.Xaml;

namespace neat;

public partial class App : Application
{
    private Window? _win;

    public App()
    {
        // Must run before any WebView2 control is created, so the bundled
        // runtime and the profile folder are picked up.
        Env.Configure();

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _win = new Win();
        _win.Activate();
    }
}
