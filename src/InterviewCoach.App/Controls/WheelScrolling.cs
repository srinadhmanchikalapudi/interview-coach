using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InterviewCoach.App.Controls;

/// <summary>
/// A multi-line TextBox with its own scrollbar swallows the mouse wheel, so moving the pointer over the job description
/// or resume box stops the page from scrolling. This makes the wheel scroll the page instead, except while the box is
/// being edited (it has keyboard focus) and still has text to scroll in the wheel's direction.
/// </summary>
public static class WheelScrolling
{
    public static readonly DependencyProperty PassToPageProperty = DependencyProperty.RegisterAttached(
        "PassToPage", typeof(bool), typeof(WheelScrolling), new PropertyMetadata(false, OnPassToPageChanged));

    public static bool GetPassToPage(DependencyObject d) => (bool)d.GetValue(PassToPageProperty);
    public static void SetPassToPage(DependencyObject d, bool value) => d.SetValue(PassToPageProperty, value);

    /// <summary>
    /// True when the text box should keep the wheel: it is focused and can still scroll in that direction.
    /// A positive delta is the wheel turned up (towards the top of the text).
    /// </summary>
    public static bool ShouldScrollInside(bool hasKeyboardFocus, int delta, double verticalOffset, double scrollableHeight)
        => hasKeyboardFocus && scrollableHeight > 0 && (delta > 0 ? verticalOffset > 0 : verticalOffset < scrollableHeight);

    private static void OnPassToPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) box.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var box = (TextBox)sender;

        var inner = box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
        if (ShouldScrollInside(box.IsKeyboardFocused, e.Delta, inner?.VerticalOffset ?? 0, inner?.ScrollableHeight ?? 0))
            return;

        var page = FindPageScrollViewer(box);
        if (page is null) return;

        e.Handled = true;
        page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = box,
        });
    }

    // The text box's own scroll viewer sits below it in the visual tree, so the first one above it is the page's.
    private static ScrollViewer? FindPageScrollViewer(DependencyObject box)
    {
        for (var node = VisualTreeHelper.GetParent(box); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer) return viewer;
        return null;
    }
}
