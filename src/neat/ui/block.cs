using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The pieces a settings page is put together from, so each page is a short
/// list of headings, cards and rows rather than a pile of layout code:
///
///   Page   a centred column that holds everything
///   Head   small bold section heading
///   Card   rounded box with a faint wash; its rows are split by 1px lines
///   Row    label (and an optional dimmer hint) on the left, a control on the right
///   Value  read-only text for the right-hand side of a row
///
/// Colours come from a <see cref="Skin"/>, so blocks follow the window colour.
/// </summary>
internal static class Block
{
    /// <summary>The column a page's content sits in. Narrow, so rows stay easy to read in a wide window.</summary>
    public static StackPanel Page(params UIElement[] parts)
    {
        var page = new StackPanel
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 24),
        };

        foreach (var part in parts)
            page.Children.Add(part);

        return page;
    }

    public static TextBlock Head(Skin s, string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = s.Fg,
            Margin = new Thickness(4, 16, 0, 8),
        };
    }

    public static Border Card(Skin s, params UIElement[] rows)
    {
        var list = new StackPanel();

        for (var i = 0; i < rows.Length; i++)
        {
            // The line goes between rows, never above the first or below the last.
            if (i > 0)
                list.Children.Add(new Border { Height = 1, Background = s.Line });

            list.Children.Add(rows[i]);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = s.Wash,
            Child = list,
        };
    }

    public static Grid Row(Skin s, string label, string? hint, FrameworkElement control)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = s.Fg,
            TextWrapping = TextWrapping.Wrap,
        });

        if (hint is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12,
                Foreground = s.Dim,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);

        var row = new Grid
        {
            MinHeight = 52,
            Padding = new Thickness(16, 10, 16, 10),
            ColumnSpacing = 16,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        row.Children.Add(control);

        return row;
    }

    /// <summary>
    /// Read-only text for the right-hand side of a row. It can be selected, so a
    /// version or a folder path can be copied into a bug report.
    /// </summary>
    public static TextBlock Value(Skin s, string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = s.Dim,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
            IsTextSelectionEnabled = true,
            MaxWidth = 340,
        };
    }
}
