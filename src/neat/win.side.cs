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
    // a page it needs a near-solid panel and an edge, or the page would show through.
    private readonly SolidColorBrush _clear = new(Colors.Transparent);
    private readonly SolidColorBrush _solid = new(ColorHelper.FromArgb(0xF2, 0x26, 0x23, 0x2D));
    private readonly SolidColorBrush _edge = new(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private bool _dock = true;
    private bool _peek;
    private bool _inSide;
    private bool _inHot;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    private void SideInit()
    {
        _dock = App.Cfg.Cur.Dock;
        Layout();

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
        side.Background = _dock ? _clear : _solid;
        side.BorderBrush = _dock ? _clear : _edge;

        // With no sidebar beside it the page gets a margin on the left too.
        frame.Margin = new Thickness(_dock ? 0 : 6, 0, 6, 6);
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
