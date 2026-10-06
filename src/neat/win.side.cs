using System.Numerics;
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
    private const double PanelW = 248;   // panel width, docked or floating (SideW minus the 6px margins)
    private const double Bite = 4;      // how far the floating panel bites into the 6px window border (top, left, bottom)
    private const int Wait = 350;       // ms the pointer may be away before a peek closes
    private const int Slide = 200;      // ms the panel takes to slide in from / out to the left edge
    private const int Fade = 120;       // ms of its fade; shorter than the slide so the panel turns solid early

    // Docked, the sidebar is just the window gradient (no panel). Floating over
    // a page it needs an opaque panel and an edge, or the page would show through.
    private readonly SolidColorBrush _clear = new(Colors.Transparent);
    private readonly SolidColorBrush _edge = new(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    // Used only if the window background is not a gradient (so there is nothing to copy).
    private readonly SolidColorBrush _solid = new(ColorHelper.FromArgb(0xFF, 0x26, 0x23, 0x2D));

    // The floating panel's brush: the window gradient, re-mapped so a given spot
    // on the panel has exactly the colour the window has at that spot.
    private readonly LinearGradientBrush _pan = new() { MappingMode = BrushMappingMode.Absolute };

    // Corner radius of the sidebar panel, read once from app.xaml.
    private double _rs;

    private bool _dock = true;
    private bool _peek;
    private bool _inSide;
    private bool _inHot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    private void SideInit()
    {
        _rs = side.CornerRadius.TopLeft;

        _dock = App.Cfg.Cur.Dock;
        Layout();

        // The panel's colours depend on the window size, so keep them in step.
        root.SizeChanged += (s, e) =>
        {
            if (!_dock)
                side.Background = PeekBrush();
        };

        // Animations are switched on only after the first Layout(), so the
        // window does not play a slide or a fade at startup.
        side.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(Fade) };
        side.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(Slide) };

        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(Wait);
        t.IsRepeating = false;
        _timer = t;
        t.Tick += (s, e) =>
        {
            t.Stop();

            // Stay open while the pointer is on the sidebar or the left edge,
            // or while the colour menu (which belongs to it) is open.
            if (_peek && !_inSide && !_inHot && !_fly)
                Peek(false);
        };

        list.ContainerContentChanging += OnRow;

        // These work while focus is in the sidebar or the title bar. Whether
        // they also fire with focus inside a web page is what W5 will find out.
        Accel(VirtualKey.S, VirtualKeyModifiers.Control, () => SetDock(!_dock));
        Accel(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Peek(!_peek));
    }

    /// <summary>Registers a shortcut for the window and adds it to the shared table (see win.keys.cs).</summary>
    private void Accel(VirtualKey key, VirtualKeyModifiers mod, Action act)
    {
        _cmds.Add(new Cmd(key, mod, act));

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

        // Hidden, the panel waits outside the left edge (the window cuts it
        // off), so showing it slides it in from the border and hiding it
        // slides it back. SideW is enough to clear it in both layouts.
        side.Translation = new Vector3(shown ? 0 : (float)-SideW, 0, 0);
        side.Opacity = shown ? 1 : 0;
        side.IsHitTestVisible = shown;

        if (_dock)
        {
            // No panel: the window gradient shows straight through.
            side.Width = PanelW;
            side.Margin = new Thickness(6);
            side.Padding = new Thickness(10);
            side.CornerRadius = new CornerRadius(_rs);
            side.Background = _clear;
            side.BorderBrush = _clear;
        }
        else
        {
            // A drawer hanging from the title bar, like Arc's. It floats over the
            // page and bites Bite pixels into the 6px border on its top, left
            // and bottom edges, so it does not sit a full border away from the
            // window the way the docked layout does, and does not touch the
            // edge either: 6 - Bite pixels are left on the left and at the
            // bottom. All four corners are rounded. On top the bite goes into
            // the title bar row, so the top corners sit on the window gradient
            // instead of on the page and no notch of page shows through them.
            //
            // The panel grows by exactly what it bites (to the left and
            // downwards, and up), and the padding grows with it, so the right
            // edge and all the content stay where they are when docked:
            // 6 + 10 from the left, 6 + 10 from the top and from the bottom.
            side.Width = PanelW + Bite;
            side.Margin = new Thickness(6 - Bite, -Bite, 0, 6 - Bite);
            side.Padding = new Thickness(10 + Bite, 16 + Bite, 10, 10 + Bite);
            side.CornerRadius = new CornerRadius(_rs);
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

        row.CornerRadius = new CornerRadius(8);
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
