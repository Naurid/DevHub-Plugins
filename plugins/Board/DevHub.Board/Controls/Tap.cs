using System.Windows.Input;
using Avalonia;
using Avalonia.Input;

namespace DevHub.Controls;

/// <summary>
/// Runs a command on a tap (press and release without moving): <c>&lt;Grid c:Tap.Command="{Binding SelectCommand}" /&gt;</c>.
/// Unlike selection (which happens on press), a drag never taps, so a row can open the issue panel on click and still
/// be dragged without the layout changing under the pointer. The DoubleTap counterpart is in the Sdk.
/// </summary>
public static class Tap
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("Command", typeof(Tap));

    static Tap()
    {
        CommandProperty.Changed.AddClassHandler<InputElement>(OnCommandChanged);
    }

    public static ICommand? GetCommand(InputElement element) => element.GetValue(CommandProperty);

    public static void SetCommand(InputElement element, ICommand? value) => element.SetValue(CommandProperty, value);

    private static void OnCommandChanged(InputElement element, AvaloniaPropertyChangedEventArgs e)
    {
        element.Tapped -= OnTapped;
        if (e.NewValue is not null)
            element.Tapped += OnTapped;
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is InputElement element && GetCommand(element) is { } command && command.CanExecute(null))
            command.Execute(null);
    }
}
