using Microsoft.UI;
using Microsoft.UI.Xaml;
using NeatBrowser.Browser;
using Windows.Graphics;

namespace NeatBrowser;

public sealed partial class MainWindow : Window
{
    private const string StartPage = "https://example.com";

    public MainWindow()
    {
        InitializeComponent();

        Title = "NEAT Browser";
        AppWindow.Resize(new SizeInt32(1280, 800));

        // Draw our own title bar: the content extends to the top edge and the
        // strip below acts as the drag area. Caption buttons stay system
        // drawn, just made transparent so they sit on our background.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // WebView2 can only start once the control is in the visual tree.
        RootGrid.Loaded += RootGrid_Loaded;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Web.EnsureCoreWebView2Async();

            var version = Web.CoreWebView2.Environment.BrowserVersionString;
            var source = WebViewRuntime.IsUsingFixedRuntime ? "bundled runtime" : "system runtime";
            StatusText.Text = $"NEAT Browser  |  WebView2 {version} ({source})";

            Web.CoreWebView2.Navigate(StartPage);
        }
        catch (Exception ex)
        {
            // There is no debugger in the loop yet, so show failures on screen.
            StatusText.Text = "WebView2 failed to start: " + ex.Message;
        }
    }
}
