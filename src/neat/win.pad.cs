using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace neat;

/// <summary>
/// The colour pad in the colour menu. The pad is a disc: the angle of the dot
/// round the centre is the colour, its distance from the centre is how light
/// the colour is (dark in the middle, light at the rim). Drag the dot, or click
/// anywhere to send it there. The maths is in <see cref="Wheel"/>.
/// </summary>
public sealed partial class Win
{
    // A press this close (in pixels) to the dot picks it up; further away it sends the dot to the press.
    private const double Grab = 16;

    private bool _drag;

    private readonly SolidColorBrush _dotFill = new();

    // The dot is moved by changing its place at once and then easing this offset back to zero,
    // which makes it glide from where it was to where it is now.
    private readonly TranslateTransform _glide = new();
    private Storyboard? _glideSb;

    private void PadInit()
    {
        dot.Fill = _dotFill;
        dot.RenderTransform = _glide;
    }

    /// <summary>The pad's width, which is also its height. Zero until the menu has been shown.</summary>
    private double PadSize() => Math.Min(square.ActualWidth, square.ActualHeight);

    /// <summary>Colours the dot and puts it where the current colour is on the pad.</summary>
    private void Square()
    {
        var t = App.Look.Cur;

        _dotFill.Color = App.Look.Dot();

        // Before the menu has been shown the pad has no size yet; the dot is placed
        // again when it gets one.
        var size = PadSize();
        if (size > 0)
        {
            var (x, y) = Wheel.ToPoint(t.Hue, t.Tone, size);
            Canvas.SetLeft(dot, x - dot.Width / 2);
            Canvas.SetTop(dot, y - dot.Height / 2);
        }
    }

    // ---- dragging the dot ----

    private void square_Down(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(square);
        if (!p.Properties.IsLeftButtonPressed || PadSize() <= 0)
            return;

        var cx = Canvas.GetLeft(dot) + dot.Width / 2;
        var cy = Canvas.GetTop(dot) + dot.Height / 2;
        var near = Math.Sqrt((p.Position.X - cx) * (p.Position.X - cx) + (p.Position.Y - cy) * (p.Position.Y - cy)) <= Grab;

        if (near)
        {
            StopGlide();
            _drag = square.CapturePointer(e.Pointer);
            Pick(p.Position);
        }
        else
        {
            Jump(p.Position);
        }
    }

    private void square_Move(object sender, PointerRoutedEventArgs e)
    {
        if (_drag)
            Pick(e.GetCurrentPoint(square).Position);
    }

    private void square_Up(object sender, PointerRoutedEventArgs e)
    {
        End(e);
    }

    private void square_Lost(object sender, PointerRoutedEventArgs e)
    {
        End(e);
    }

    /// <summary>
    /// Sets the colour to the spot under the pointer. A point outside the disc
    /// counts as the nearest point on its rim. Not saved on every movement;
    /// End() saves once when the drag is over.
    /// </summary>
    private void Pick(Windows.Foundation.Point p)
    {
        var (angle, radius) = Wheel.FromPoint(p.X, p.Y, PadSize());

        App.Look.Set(t =>
        {
            t.Hue = angle;
            t.Tone = radius;
        }, save: false);
    }

    /// <summary>A click away from the dot: the colour changes at once, and the dot glides there.</summary>
    private void Jump(Windows.Foundation.Point p)
    {
        StopGlide();

        var left = Canvas.GetLeft(dot);
        var top = Canvas.GetTop(dot);

        Pick(p);   // Square() runs through the Changed event and moves the dot

        Glide(left - Canvas.GetLeft(dot), top - Canvas.GetTop(dot));
        App.Look.Commit();
    }

    private void End(PointerRoutedEventArgs e)
    {
        if (!_drag)
            return;

        _drag = false;
        square.ReleasePointerCapture(e.Pointer);
        App.Look.Commit();
    }

    // ---- the glide ----

    /// <summary>Eases the dot from an offset of (dx, dy) back to where it is, with a small overshoot.</summary>
    private void Glide(double dx, double dy)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            return;

        _glide.X = dx;
        _glide.Y = dy;

        var sb = new Storyboard();
        Ease(sb, "X", dx);
        Ease(sb, "Y", dy);

        _glideSb = sb;
        sb.Begin();
    }

    private void Ease(Storyboard sb, string property, double from)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        };

        Storyboard.SetTarget(a, _glide);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
    }

    private void StopGlide()
    {
        _glideSb?.Stop();
        _glideSb = null;
        _glide.X = 0;
        _glide.Y = 0;
    }
}
