using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.System;

namespace neat;

public sealed partial class Win : Window
{
    private readonly ObservableCollection<Tab> _tabs = new();
    private Tab? _cur;
    private bool _edit;   // command bar mode: false = open in a new tab, true = edit this tab's address
    private string _ver = string.Empty;

    public Win()
    {
        InitializeComponent();

        Title = "NEAT Browser";

        var geo = App.Cfg.Cur.Geo;
        AppWindow.Resize(new SizeInt32(geo.W, geo.H));
        Closed += OnClosed;

        BarInit();

        list.ItemsSource = _tabs;
        SideInit();
        KeysInit();
        PaintInit();

        // The first tab opens once the window is up.
        root.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Cfg.Cur.Geo.Max && AppWindow.Presenter is OverlappedPresenter p)
            p.Maximize();

        // Watches for the UI thread getting stuck (see Heartbeat in web/log.cs).
        // The queue is taken here, on the UI thread, and used from the timer's thread.
        var ui = DispatcherQueue;
        Heartbeat.Start(a => ui.TryEnqueue(() => a()));

        var tab = await Open(App.Cfg.Cur.Home);

        var core = tab.View.CoreWebView2;
        if (core is not null)
        {
            var ver = core.Environment.BrowserVersionString;
            WebVer = ver;   // for the settings window's About page (win.opts.cs)
            var src = Env.UsesFixed ? "bundled runtime" : "system runtime";
            _ver = $"WebView2 {ver} ({src})";
            Log.Write("webview", _ver);
        }

        await Stats();
    }

    /// <summary>Remembers the window size (and whether it was maximized) for next time.</summary>
    private void OnClosed(object sender, WindowEventArgs e)
    {
        // A normal exit leaves this line; a crash does not.
        Log.Write("app", "window closed normally");
        Heartbeat.Stop();

        // A closed window must stop repainting itself when the colour changes.
        App.Look.Changed -= Paint;

        if (AppWindow.Presenter is not OverlappedPresenter p)
            return;

        var geo = App.Cfg.Cur.Geo;
        geo.Max = p.State == OverlappedPresenterState.Maximized;

        // Only a normal window has a size worth keeping; a maximized or
        // minimized one would save the wrong numbers.
        if (p.State == OverlappedPresenterState.Restored)
        {
            geo.W = AppWindow.Size.Width;
            geo.H = AppWindow.Size.Height;
        }

        App.Cfg.Save();
    }

    // ---- tabs ----

    /// <summary>Opens a new tab, selects it and starts loading the address.</summary>
    private async Task<Tab> Open(string? url)
    {
        var tab = new Tab(new WebView2());
        host.Children.Add(tab.View);
        _tabs.Add(tab);
        Log.Tabs = _tabs.Count;
        Log.Write("tab", $"open, tabs now {_tabs.Count}");
        Pick(tab);

        try
        {
            await tab.View.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            // No debugger in the loop yet, so show failures on screen.
            tab.Title = "WebView2 failed: " + ex.Message;
            Log.Error("webview", ex, "EnsureCoreWebView2Async failed");
            return tab;
        }

        var core = tab.View.CoreWebView2;

        // The tab can be closed while its web view is still starting up. Then
        // there is no CoreWebView2 left to hook, and hooking it would throw.
        if (core is null)
        {
            Log.Write("tab", "web view was closed before it finished starting");
            return tab;
        }

        Hook(tab, core);

        // Must be in place before the first page loads. Without it only the
        // shortcuts for the window's own controls work, so a failure here
        // is logged and browsing carries on.
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Js());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[keys] script failed: " + ex.Message);
        }

        var to = App.Find.Resolve(url ?? App.Cfg.Cur.Home);
        if (to is not null)
            core.Navigate(to);

        return tab;
    }

    /// <summary>Keeps a tab's sidebar row and the address box in step with its page.</summary>
    private void Hook(Tab tab, CoreWebView2 core)
    {
        // A web view process (renderer, GPU or the whole browser) that dies
        // is written to crash.log. Nothing else reacts to it yet.
        core.ProcessFailed += (s, e) => Log.Write("webview",
            $"process failed: {e.ProcessFailedKind}, reason {e.Reason}, exit code {e.ExitCode}, {e.ProcessDescription}; tabs {_tabs.Count}");

        core.WebMessageReceived += (s, e) => OnKey(tab, e.TryGetWebMessageAsString());

        core.DocumentTitleChanged += (s, e) =>
        {
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrEmpty(title) ? core.Source : title;
        };

        core.SourceChanged += (s, e) =>
        {
            tab.Src = core.Source;
            if (tab == _cur)
            {
                ShowDom(tab);
                _ = Star();
            }
        };

        // Every page that finishes loading is recorded in history.
        core.NavigationCompleted += async (s, e) =>
        {
            var url = core.Source;
            var web = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!e.IsSuccess || !web)
                return;

            await Log.Timed("history.add", () => App.Hist.Add(url, core.DocumentTitle, tab.IconSrc));
            await Stats();
        };

        core.FaviconChanged += (s, e) => tab.SetIcon(core.FaviconUri);

        // Links that ask for a new window (target=_blank, ctrl+click) become new tabs.
        core.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            Log.Write("tab", $"page asked for a new window, tabs now {_tabs.Count}");
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

        ShowDom(tab);
        _ = Star();
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
        Log.Tabs = _tabs.Count;
        Log.Write("tab", $"close, tabs now {_tabs.Count}");
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

    // ---- navigation buttons (always act on the selected tab) ----

    private void back_Click(object sender, RoutedEventArgs e)
    {
        Back();
    }

    private void Back()
    {
        if (_cur is { View.CanGoBack: true })
            _cur.View.GoBack();
    }

    private void fwd_Click(object sender, RoutedEventArgs e)
    {
        Fwd();
    }

    private void Fwd()
    {
        if (_cur is { View.CanGoForward: true })
            _cur.View.GoForward();
    }

    private void reload_Click(object sender, RoutedEventArgs e)
    {
        _cur?.View.Reload();
    }

    // ---- bookmarks and stats ----

    /// <summary>Shows a filled star when the selected tab's page is bookmarked.</summary>
    private async Task Star()
    {
        var url = _cur?.Src;
        var on = false;

        try
        {
            on = !string.IsNullOrEmpty(url) && await Log.TimedValue("bookmarks.has", () => App.Marks.Has(url!));
        }
        catch (Exception)
        {
            // Treated as "not bookmarked"; Stats() reports database problems.
        }

        // The user may have switched tabs while the lookup ran.
        if (url == _cur?.Src)
            staric.Glyph = on ? "\uE735" : "\uE734";
    }

    private async void star_Click(object sender, RoutedEventArgs e)
    {
        await ToggleMark();
    }

    /// <summary>Bookmarks the selected tab's page, or removes the bookmark if it already has one.</summary>
    private async Task ToggleMark()
    {
        var tab = _cur;
        if (tab is null || string.IsNullOrEmpty(tab.Src))
            return;

        try
        {
            if (await App.Marks.Has(tab.Src))
                await App.Marks.Remove(tab.Src);
            else
                await App.Marks.Add(tab.Src, tab.Title, tab.IconSrc);
        }
        catch (Exception ex)
        {
            info.Text = _ver + "\nbookmark failed: " + ex.Message;
            return;
        }

        await Star();
        await Stats();
    }

    /// <summary>
    /// Temporary footer line so the new data layer can be seen working: how
    /// many pages are in history and how many are bookmarked.
    /// </summary>
    private async Task Stats()
    {
        try
        {
            var h = await Log.TimedValue("history.count", () => App.Hist.Count());
            var m = await Log.TimedValue("bookmarks.count", () => App.Marks.Count());
            info.Text = $"{_ver}\nhistory {h}  |  bookmarks {m}";
        }
        catch (Exception ex)
        {
            info.Text = _ver + "\ndata error: " + ex.Message;
        }
    }

    // ---- command bar overlay ----

    /// <summary>
    /// Opens the command bar. Two modes: new tab (empty box, Enter opens the
    /// result in a new tab) or edit (the selected tab's address filled in and
    /// selected, Enter navigates that tab).
    /// </summary>
    private void ShowBar(bool edit)
    {
        _edit = edit;
        q.Text = edit ? (_cur?.Src ?? string.Empty) : string.Empty;
        scrim.Visibility = Visibility.Visible;

        // The box only accepts focus once it is laid out.
        DispatcherQueue.TryEnqueue(() =>
        {
            q.Focus(FocusState.Programmatic);

            if (edit)
                q.SelectAll();
        });
    }

    private void HideBar()
    {
        scrim.Visibility = Visibility.Collapsed;
    }

    private void newtab_Click(object sender, RoutedEventArgs e)
    {
        ShowBar(false);
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
            var url = App.Find.Resolve(q.Text);
            var edit = _edit;
            var tab = _cur;
            HideBar();

            if (url is null)
                return;

            // Edit mode goes to the page of the selected tab, new-tab mode
            // opens a tab. A tab whose web view has not started yet cannot be
            // navigated, so it falls back to a new tab.
            var core = tab?.View.CoreWebView2;
            if (edit && tab is not null && core is not null)
            {
                core.Navigate(url);
                tab.View.Focus(FocusState.Programmatic);
            }
            else
            {
                _ = Open(url);
            }
        }
    }
}
