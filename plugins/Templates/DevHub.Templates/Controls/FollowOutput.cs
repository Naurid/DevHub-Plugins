using Avalonia;
using Avalonia.Controls;

namespace DevHub.Controls;

/// <summary>
/// Keeps a <see cref="ScrollViewer"/> at the bottom while its content grows (a live command log),
/// like a terminal: <c>c:FollowOutput.IsEnabled="True"</c>. Scrolling up to read stops following;
/// scrolling back to the bottom starts it again.
/// </summary>
public static class FollowOutput
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(FollowOutput));

    static FollowOutput()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            viewer.ScrollChanged -= OnScrollChanged;
            if (e.NewValue is true)
                viewer.ScrollChanged += OnScrollChanged;
        });
    }

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(IsEnabledProperty, value);

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ExtentDelta.Y <= 0)
            return;
        // Was it at the bottom before the content grew? (A small tolerance for rounding.)
        var bottomBefore = viewer.Extent.Height - e.ExtentDelta.Y;
        if (viewer.Offset.Y + viewer.Viewport.Height >= bottomBefore - 4)
            viewer.ScrollToEnd();
    }
}
