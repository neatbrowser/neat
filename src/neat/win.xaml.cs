using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.System;

namespace neat;

public sealed partial class Win : Window
{
    private const string Home = "https://example.com";

    private readonly ObservableCollection<Tab> _tabs = new();
    private Tab? _cur;

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

        list.ItemsSource = _tabs;

        // The first tab opens once the window is up.
        root.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var tab = await Open(Home);

        var core = tab.View.CoreWebView2;
        if (core is not null)
        {
            var ver = core.Environment.BrowserVersionString;
            var src = Env.UsesFixed ? "bundled runtime" : "system runtime";
            info.Text = $"WebView2 {ver} ({src})";
        }
    }

    // ---- tabs ----

    /// <summary>Opens a new tab, selects it and starts loading the address.</summary>
    private async Task<Tab> Open(string? url)
    {
        var tab = new Tab(new WebView2());
        host.Children.Add(tab.View);
        _tabs.Add(tab);
        Pick(tab);

        try
        {
            await tab.View.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            // No debugger in the loop yet, so show failures on screen.
            tab.Title = "WebView2 failed: " + ex.Message;
            return tab;
        }

        var core = tab.View.CoreWebView2;
        Hook(tab, core);
        core.Navigate(url ?? Home);
        return tab;
    }

    /// <summary>Keeps a tab's sidebar row and the address box in step with its page.</summary>
    private void Hook(Tab tab, CoreWebView2 core)
    {
        core.DocumentTitleChanged += (s, e) =>
        {
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrEmpty(title) ? core.Source : title;
        };

        core.SourceChanged += (s, e) =>
        {
            tab.Src = core.Source;
            if (tab == _cur)
                addr.Text = tab.Src;
        };

        core.FaviconChanged += (s, e) => tab.SetIcon(core.FaviconUri);

        // Links that ask for a new window (target=_blank, ctrl+click) become new tabs.
        core.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            _ = Open(e.Uri);
        };

        // window.close() from the page closes its tab. Deferred, because the
        // view must not be torn down from inside its own event.
        core.WindowCloseRequested += (s, e) =>
            DispatcherQueue.TryEnqueue(() => Shut(tab));
    }

    /// <summary>Makes a tab the visible one.</summary>
    private void Pick(Tab tab)
    {
        _cur = tab;

        if (list.SelectedItem != tab)
            list.SelectedItem = tab;

        foreach (var t in _tabs)
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;

        addr.Text = tab.Src;
    }

    /// <summary>Closes a tab. Closing the last one closes the window.</summary>
    private void Shut(Tab tab)
    {
        var i = _tabs.IndexOf(tab);
        if (i < 0)
            return;

        if (_tabs.Count == 1)
        {
            Close();
            return;
        }

        host.Children.Remove(tab.View);
        _tabs.RemoveAt(i);
        tab.View.Close();

        if (tab == _cur)
            Pick(_tabs[Math.Min(i, _tabs.Count - 1)]);
    }

    private void list_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (list.SelectedItem is Tab tab && tab != _cur)
            Pick(tab);
    }

    private void shut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is Tab tab)
            Shut(tab);
    }

    // ---- navigation (always acts on the selected tab) ----

    private void Go(string? text)
    {
        var url = Url.Fix(text);
        var core = _cur?.View.CoreWebView2;
        if (url is null || core is null)
            return;

        core.Navigate(url);
    }

    private void back_Click(object sender, RoutedEventArgs e)
    {
        if (_cur is { View.CanGoBack: true })
            _cur.View.GoBack();
    }

    private void fwd_Click(object sender, RoutedEventArgs e)
    {
        if (_cur is { View.CanGoForward: true })
            _cur.View.GoForward();
    }

    private void reload_Click(object sender, RoutedEventArgs e)
    {
        _cur?.View.Reload();
    }

    private void addr_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        Go(addr.Text);
        _cur?.View.Focus(FocusState.Programmatic);
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
            // Enter in the command bar opens the result in a NEW tab.
            var url = Url.Fix(q.Text);
            HideBar();

            if (url is not null)
                _ = Open(url);
        }
    }
}
