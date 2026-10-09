using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace neat;

/// <summary>
/// The settings window. One at a time (Win keeps the reference, see win.opts.cs),
/// opened with Ctrl+, or the gear in the sidebar footer.
///
/// It has no title text, no minimize or maximize and cannot be resized: a thin
/// empty strip along the top (drag it to move the window), the system close
/// button, and under that a row of tabs, icon above label, like Arc's. The
/// pages are built in code from the blocks in ui/block.cs; opts.gen.cs and
/// opts.about.cs hold the pages themselves.
///
/// Its colours come from the same window colour as the browser window
/// (App.Look), through a Skin of its own, and follow it live.
/// </summary>
public sealed partial class Opts : Window
{
    // Window size in DIPs. AppWindow wants physical pixels, so this is scaled.
    private const int W = 760;
    private const int H = 640;

    // Tab name and its glyph. All four are in both Segoe Fluent Icons and Segoe MDL2 Assets
    // (Windows 10 only has the second): Setting, Color, KeyboardClassic and Info.
    private static readonly (string Name, string Glyph)[] Pages =
    {
        ("General", "\uE713"),
        ("Appearance", "\uE790"),
        ("Shortcuts", "\uE765"),
        ("About", "\uE946"),
    };

    private readonly Win _own;
    private readonly Skin _skin = new();
    private readonly SolidColorBrush _none = new(Colors.Transparent);
    private readonly List<Button> _btns = new();

    // The page on show, -1 before the first one.
    private int _at = -1;

    public Opts(Win own)
    {
        InitializeComponent();
        _own = own;

        // No title text anywhere: not in the window, the task bar or Alt+Tab.
        Title = string.Empty;

        // The content reaches the top edge; the empty strip is the drag area.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(strip);

        // Only the system close button should be left.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMinimizable = false;
            p.IsMaximizable = false;
            p.IsResizable = false;
        }

        Place(own);

        // The skin's brush is changed in place by Paint, so it is set once.
        root.Background = _skin.Bg;
        BuildTabs();

        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (s, e) =>
        {
            // With the engine list open, Esc is for the list; it must not take the window with it.
            if (_box is { IsDropDownOpen: true })
                return;

            e.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(esc);

        // The browser window closing must take this one with it, or the
        // app would keep running with only the settings window open.
        own.Closed += OnOwnClosed;
        Closed += OnClosed;

        App.Look.Changed += Paint;
        Paint();

        Pick(0);
    }

    /// <summary>
    /// Sizes the window and centres it over the browser window, kept on the
    /// screen the browser window is on.
    /// </summary>
    private void Place(Win own)
    {
        // Resize takes physical pixels. The scale of the browser window is used
        // because this window opens on the same screen.
        var k = own.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var w = (int)Math.Round(W * k);
        var h = (int)Math.Round(H * k);

        var o = own.AppWindow;
        var area = DisplayArea.GetFromWindowId(o.Id, DisplayAreaFallback.Nearest).WorkArea;

        var x = o.Position.X + (o.Size.Width - w) / 2;
        var y = o.Position.Y + (o.Size.Height - h) / 2;

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - w));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - h));

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    // ---- tabs ----

    private void BuildTabs()
    {
        for (var i = 0; i < Pages.Length; i++)
        {
            var at = i;

            var col = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };

            // No FontFamily: the default picks Segoe Fluent Icons, or Segoe MDL2 Assets where it is missing.
            col.Children.Add(new FontIcon { Glyph = Pages[i].Glyph, FontSize = 20, Foreground = _skin.Fg });
            col.Children.Add(new TextBlock
            {
                Text = Pages[i].Name,
                FontSize = 12,
                Foreground = _skin.Fg,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var b = new Button
            {
                Width = 88,
                Height = 64,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Background = _none,
                Content = col,
            };
            b.Click += (s, e) => Pick(at);

            _btns.Add(b);
            tabs.Children.Add(b);
        }
    }

    /// <summary>Shows a page and marks its tab.</summary>
    private void Pick(int i)
    {
        if (i == _at)
            return;

        // Whatever is typed in the page being left is kept before it goes.
        Flush();
        _home = null;
        _box = null;

        _at = i;
        for (var n = 0; n < _btns.Count; n++)
            _btns[n].Background = n == i ? _skin.Pill : _none;

        // Rebuilt each time, so a page always shows the current values (the
        // WebView2 version, for one, is not known until the first tab has loaded).
        scroll.Content = i switch
        {
            0 => BuildGen(),
            3 => BuildAbout(),
            _ => Block.Page(),   // Appearance and Shortcuts come in W7.2
        };
        scroll.ChangeView(0, 0, null, true);
    }

    // ---- colour ----

    /// <summary>Applies the window colour: the skin, the theme and the system buttons.</summary>
    private void Paint()
    {
        var t = App.Look.Cur;
        _skin.Apply(t);

        // The controls on the pages (text boxes, drop-downs) follow this.
        root.RequestedTheme = t.Dark ? ElementTheme.Dark : ElementTheme.Light;

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        // The close button sits straight on the background and is told what to
        // look like, because on its own it follows the app's theme, not this window's.
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = _skin.Fg.Color;
        tb.ButtonInactiveForegroundColor = _skin.Dim.Color;
        tb.ButtonHoverBackgroundColor = _skin.Hover;
        tb.ButtonHoverForegroundColor = _skin.Fg.Color;
        tb.ButtonPressedBackgroundColor = _skin.Press;
        tb.ButtonPressedForegroundColor = _skin.Fg.Color;
    }

    // ---- closing ----

    private void OnOwnClosed(object sender, WindowEventArgs e)
    {
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        // A closed window must stop repainting itself when the colour changes.
        App.Look.Changed -= Paint;
        _own.Closed -= OnOwnClosed;

        // Esc and the close button do not move focus first, so a half-typed
        // home page would be lost without this.
        Flush();
    }
}
