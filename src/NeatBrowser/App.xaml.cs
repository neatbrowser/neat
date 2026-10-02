using Microsoft.UI.Xaml;
using NeatBrowser.Browser;

namespace NeatBrowser;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // Must run before any WebView2 control is created, so the bundled
        // runtime and the profile folder are picked up.
        WebViewRuntime.Configure();

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
