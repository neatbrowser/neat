using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace neat;

/// <summary>
/// Sidebar behaviour. The sidebar is one element with three states:
///   docked  - beside the page, the page shrinks to make room;
///   hidden  - gone, the page fills the window;
///   peeking - hidden, but floating over the page while the pointer rests on
///             the left edge (or on the sidebar itself).
/// Ctrl+S flips docked/hidden and the choice is remembered in settings.json.
/// </summary>
public sealed partial class Win
{
    private const double SideW = 260;   // column reserved while docked (panel + margins)
    private const int Wait = 350;       // ms the pointer may be away before a peek closes

    // Docked, the sidebar is just the window gradient (no panel). Floating over
    // a page it needs an opaque panel and an edge, or the page would show through.
    private readonly SolidColorBrush _clear = new(Colors.Transparent);
    private readonly SolidColorBrush _edge = new(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    // Used only if the window background is not a gradient (so there is nothing to copy).
    private readonly SolidColorBrush _solid = new(ColorHelper.FromArgb(0xFF, 0x26, 0x23, 0x2D));

    // The floating panel's brush: the window gradient, re-mapped so a given spot
    // on the panel has exactly the colour the window has at that spot.
    private readonly LinearGradientBrush _pan = new() { MappingMode = BrushMappingMode.Absolute };

    // Corner radii from app.xaml, read once: sidebar panel and web frame.
    private double _rs;
    private double _rw;

    private bool _dock = true;
    private bool _peek;
    private bool _inSide;
    private bool _inHot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    private void SideInit()
    {
        _rs = side.CornerRadius.TopLeft;
        _rw = frame.CornerRadius.TopLeft;

        _dock = App.Cfg.Cur.Dock;
        Layout();

        // The panel's colours depend on the window size, so keep them in step.
        root.SizeChanged += (s, e) =>
        {
            if (!_dock)
                side.Background = PeekBrush();
        };

        // Animations are switched on only after the first Layout(), so the
        // window does not play a fade at startup.
        var fade = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        side.OpacityTransition = fade;

        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(Wait);
        t.IsRepeating = false;
        _timer = t;
        t.Tick += (s, e) =>
        {
            t.Stop();

            // Stay open while the pointer is on the sidebar or the left edge.
            if (_peek && !_inSide && !_inHot)
                Peek(false);
        };

        list.ContainerContentChanging += OnRow;

        // These work while focus is in the sidebar or the title bar. Whether
        // they also fire with focus inside a web page is what W5 will find out.
        Accel(VirtualKey.S, VirtualKeyModifiers.Control, () => SetDock(!_dock));
        Accel(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Peek(!_peek));
    }

    private void Accel(VirtualKey key, VirtualKeyModifiers mod, Action act)
    {
        var k = new KeyboardAccelerator { Key = key, Modifiers = mod };
        k.Invoked += (s, e) =>
        {
            e.Handled = true;
            act();
        };
        root.KeyboardAccelerators.Add(k);
    }

    /// <summary>Applies the current state to every piece of the window that depends on it.</summary>
    private void Layout()
    {
        var shown = _dock || _peek;

        // Column 0 only reserves room for a docked sidebar; a peeking one
        // floats over the page and takes none.
        col0.Width = new GridLength(_dock ? SideW : 0);
        hot.Visibility = _dock ? Visibility.Collapsed : Visibility.Visible;

        side.Opacity = shown ? 1 : 0;
        side.IsHitTestVisible = shown;

        if (_dock)
        {
            // No panel: the window gradient shows straight through.
            side.Margin = new Thickness(6);
            side.Padding = new Thickness(10);
            side.CornerRadius = new CornerRadius(_rs);
            side.Background = _clear;
            side.BorderBrush = _clear;
        }
        else
        {
            // A drawer hanging from the title bar. It starts exactly where the
            // page starts (no top margin, so no strip of page shows above it),
            // and its left corners copy the page frame's corners so the two
            // line up. The extra top padding keeps the content where it is
            // when docked.
            side.Margin = new Thickness(6, 0, 6, 6);
            side.Padding = new Thickness(10, 16, 10, 10);
            side.CornerRadius = new CornerRadius(_rw, 0, _rs, _rw);
            side.Background = PeekBrush();
            side.BorderBrush = _edge;
        }

        // With no sidebar beside it the page gets a margin on the left too.
        frame.Margin = new Thickness(_dock ? 0 : 6, 0, 6, 6);
    }

    /// <summary>
    /// The floating panel's background: the window gradient, opaque. A brush
    /// normally stretches over the element it paints, which would give the
    /// narrow panel a different colour than the window has at the same spot. So
    /// the colours are copied and the gradient line is expressed in the window's
    /// coordinates, shifted to where the panel sits.
    /// </summary>
    private Brush PeekBrush()
    {
        if (root.Background is not LinearGradientBrush g || root.ActualWidth <= 0 || root.ActualHeight <= 0)
            return _solid;

        _pan.GradientStops.Clear();
        foreach (var st in g.GradientStops)
            _pan.GradientStops.Add(new GradientStop { Color = st.Color, Offset = st.Offset });

        // Where the panel's top-left corner sits in the window.
        var x = side.Margin.Left;
        var y = bar.ActualHeight + side.Margin.Top;

        var w = root.ActualWidth;
        var h = root.ActualHeight;
        _pan.StartPoint = new Windows.Foundation.Point(g.StartPoint.X * w - x, g.StartPoint.Y * h - y);
        _pan.EndPoint = new Windows.Foundation.Point(g.EndPoint.X * w - x, g.EndPoint.Y * h - y);

        return _pan;
    }

    private void SetDock(bool on)
    {
        _dock = on;
        _peek = false;

        App.Cfg.Cur.Dock = on;
        App.Cfg.Save();

        Layout();
    }

    /// <summary>Floats the sidebar over the page, or puts it away again. Does nothing while docked.</summary>
    private void Peek(bool on)
    {
        if (_dock || _peek == on)
            return;

        _peek = on;
        Layout();
    }

    /// <summary>Starts the countdown that closes a peek once the pointer is away.</summary>
    private void Leave()
    {
        if (_peek)
            _timer?.Start();
    }

    private void tog_Click(object sender, RoutedEventArgs e)
    {
        SetDock(!_dock);
    }

    private void hot_In(object sender, PointerRoutedEventArgs e)
    {
        _inHot = true;
        _timer?.Stop();
        Peek(true);
    }

    private void hot_Out(object sender, PointerRoutedEventArgs e)
    {
        _inHot = false;
        Leave();
    }

    private void side_In(object sender, PointerRoutedEventArgs e)
    {
        _inSide = true;
        _timer?.Stop();
    }

    private void side_Out(object sender, PointerRoutedEventArgs e)
    {
        _inSide = false;
        Leave();
    }

    // ---- tab rows ----

    /// <summary>Gives every tab row the same soft, rounded look.</summary>
    private void OnRow(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer is not ListViewItem row)
            return;

        row.CornerRadius = new CornerRadius(10);
        row.Margin = new Thickness(0, 1, 0, 1);
        row.Padding = new Thickness(8, 0, 4, 0);
        row.MinHeight = 0;
    }

    private void row_In(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Visible);
    }

    private void row_Out(object sender, PointerRoutedEventArgs e)
    {
        Cross(sender, Visibility.Collapsed);
    }

    /// <summary>Shows or hides a row's close button.</summary>
    private static void Cross(object row, Visibility v)
    {
        if (row is not Grid g)
            return;

        foreach (var b in g.Children.OfType<Button>())
            b.Visibility = v;
    }
}
