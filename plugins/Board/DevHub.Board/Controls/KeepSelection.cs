using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DevHub.Controls;

/// <summary>
/// Lets several selected rows be dragged together: <c>&lt;ListBox c:KeepSelection.IsEnabled="True" /&gt;</c>. A plain
/// press on a row that's part of a selection of several would select that row alone before the drag starts; this keeps
/// the selection on the press, and selects the row alone on release when it wasn't dragged (an ordinary click).
/// </summary>
public static class KeepSelection
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("IsEnabled", typeof(KeepSelection));

    // The row pressed while a selection was kept, and where: released close by, it was a click.
    private static ListBoxItem? _held;
    private static Point _pressedAt;

    static KeepSelection()
    {
        IsEnabledProperty.Changed.AddClassHandler<ListBox>(OnChanged);
    }

    public static bool GetIsEnabled(ListBox list) => list.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ListBox list, bool value) => list.SetValue(IsEnabledProperty, value);

    private static void OnChanged(ListBox list, AvaloniaPropertyChangedEventArgs e)
    {
        list.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        list.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        if (e.NewValue is true)
        {
            list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _held = null;
        if (sender is not ListBox list || e.KeyModifiers != KeyModifiers.None || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed
            || list.SelectedItems is not { Count: > 1 } || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { IsSelected: true } item)
            return;
        _held = item;
        _pressedAt = e.GetPosition(list);
        e.Handled = true;   // the drag (ItemsDragDrop listens to handled presses too) still starts
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_held is not { } item || sender is not ListBox list)
            return;
        _held = null;
        var moved = e.GetPosition(list) - _pressedAt;
        if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4)
            list.SelectedItem = list.ItemFromContainer(item);
    }
}
