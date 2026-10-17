using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DevHub.Controls;

/// <summary>
/// Ctrl+click (or Shift+click) on a card picks it into a selection of several (to drag or edit together):
/// <c>&lt;Border c:CardPick.Command="{Binding PickCommand}" /&gt;</c>. The press is handled before the card's own button
/// sees it, so it neither selects the card alone nor starts a drag.
/// </summary>
public static class CardPick
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("Command", typeof(CardPick));

    static CardPick()
    {
        CommandProperty.Changed.AddClassHandler<InputElement>(OnCommandChanged);
    }

    public static ICommand? GetCommand(InputElement element) => element.GetValue(CommandProperty);

    public static void SetCommand(InputElement element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(InputElement element, AvaloniaPropertyChangedEventArgs e)
    {
        element.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        if (e.NewValue is not null)
            element.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        const KeyModifiers pickKeys = KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift;
        if (sender is not InputElement element || (e.KeyModifiers & pickKeys) == 0
            || !e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
            return;
        if (GetCommand(element) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
