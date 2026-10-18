using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DevHub.Controls;

/// <summary>
/// Runs a command when a press lands on empty space (not on a row, a button, a box or a scroll bar):
/// <c>&lt;ScrollViewer c:EmptyClick.Command="{Binding ClearSelectionCommand}" /&gt;</c>. The backlog unselects this way.
/// </summary>
public static class EmptyClick
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(EmptyClick));

    static EmptyClick()
    {
        CommandProperty.Changed.AddClassHandler<Control>(OnChanged);
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        if (e.NewValue is not null)
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed
            || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source)
            return;
        // Anything you can click on keeps the press for itself.
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null || source.FindAncestorOfType<Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null
            || source.FindAncestorOfType<ComboBox>(includeSelf: true) is not null || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
            return;
        if (GetCommand(control) is { } command && command.CanExecute(null))
            command.Execute(null);
    }
}
