using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace neat;

public sealed partial class Win : Window
{
    private const string Home = "https://example.com";

    public Win()
    {
        InitializeComponent();

        Title = "NEAT Browser";
        AppWindow.Resize(new SizeInt32(1280, 800));

        // Draw our own title bar: content extends to the top edge and the thin
        // strip in the content column is the drag area. Caption buttons stay
        // system drawn, made transparent so they sit on our background.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(drag);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // WebView2 can only start once the control is in the visual tree.
        root.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await web.EnsureCoreWebView2Async();

            var ver = web.CoreWebView2.Environment.BrowserVersionString;
            var src = Env.UsesFixed ? "bundled runtime" : "system runtime";
            info.Text = $"WebView2 {ver} ({src})";

            web.CoreWebView2.Navigate(Home);
        }
        catch (Exception ex)
        {
            // No debugger in the loop yet, so show failures on screen.
            info.Text = "WebView2 failed to start: " + ex.Message;
        }
    }

    // ---- navigation ----

    private void Go(string? text)
    {
        var url = Url.Fix(text);
        if (url is null || web.CoreWebView2 is null)
            return;

        web.CoreWebView2.Navigate(url);
    }

    private void back_Click(object sender, RoutedEventArgs e)
    {
        if (web.CanGoBack)
            web.GoBack();
    }

    private void fwd_Click(object sender, RoutedEventArgs e)
    {
        if (web.CanGoForward)
            web.GoForward();
    }

    private void reload_Click(object sender, RoutedEventArgs e)
    {
        web.Reload();
    }

    private void addr_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        Go(addr.Text);
        web.Focus(FocusState.Programmatic);
    }

    // ---- command bar overlay ----

    private void ShowBar()
    {
        q.Text = string.Empty;
        scrim.Visibility = Visibility.Visible;

        // The box only accepts focus once it is laid out.
        DispatcherQueue.TryEnqueue(() => q.Focus(FocusState.Programmatic));
    }

    private void HideBar()
    {
        scrim.Visibility = Visibility.Collapsed;
    }

    private void newtab_Click(object sender, RoutedEventArgs e)
    {
        ShowBar();
    }

    private void scrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        HideBar();
    }

    private void panel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Keep taps on the panel from reaching the scrim and closing it.
        e.Handled = true;
    }

    private void q_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideBar();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            var text = q.Text;
            HideBar();
            Go(text);
        }
    }
}
