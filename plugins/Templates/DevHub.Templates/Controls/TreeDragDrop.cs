using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DevHub.Controls;

/// <summary>What a tree's drag and drop does (implemented by its ViewModel): may this item go there, and do it.</summary>
public interface ITreeDropHandler
{
    /// <param name="target">The item dropped on, or null for the tree's empty space (the top level).</param>
    bool CanDrop(object source, object? target);

    void Drop(object source, object? target);
}

/// <summary>
/// Drag and drop between the rows of a <see cref="TreeView"/>, decided by the ViewModel:
/// <c>c:TreeDragDrop.Handler="{Binding}"</c> with a ViewModel that's an <see cref="ITreeDropHandler"/>.
/// A drag starts when the pointer moves a few pixels with the left button down on a row; the row
/// under the pointer gets the <c>dropTarget</c> class while the drop is allowed there. Only rows of
/// the same tree are carried (an in-process format), never files from outside.
/// </summary>
public static class TreeDragDrop
{
    public static readonly AttachedProperty<ITreeDropHandler?> HandlerProperty =
        AvaloniaProperty.RegisterAttached<TreeView, ITreeDropHandler?>("Handler", typeof(TreeDragDrop));

    private static readonly DataFormat<object> RowFormat = DataFormat.CreateInProcessFormat<object>("DevHub.TreeRow");
    private const double StartDistance = 4;

    // One drag at a time in the whole app, so the pending press can live here.
    private static PointerPressedEventArgs? _pressed;
    private static Point _pressedAt;
    private static object? _pressedItem;
    private static TreeViewItem? _highlighted;

    static TreeDragDrop()
    {
        HandlerProperty.Changed.AddClassHandler<TreeView>((tree, e) =>
        {
            Detach(tree);
            if (e.NewValue is not null)
                Attach(tree);
        });
    }

    public static ITreeDropHandler? GetHandler(TreeView tree) => tree.GetValue(HandlerProperty);

    public static void SetHandler(TreeView tree, ITreeDropHandler? value) => tree.SetValue(HandlerProperty, value);

    private static void Attach(TreeView tree)
    {
        // handledEventsToo: the row handles the press itself (selection); the drag still needs to see it.
        tree.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        tree.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        tree.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        DragDrop.SetAllowDrop(tree, true);
        DragDrop.AddDragOverHandler(tree, OnDragOver);
        DragDrop.AddDragLeaveHandler(tree, OnDragLeave);
        DragDrop.AddDropHandler(tree, OnDrop);
    }

    private static void Detach(TreeView tree)
    {
        tree.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        tree.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        tree.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        DragDrop.SetAllowDrop(tree, false);
        DragDrop.RemoveDragOverHandler(tree, OnDragOver);
        DragDrop.RemoveDragLeaveHandler(tree, OnDragLeave);
        DragDrop.RemoveDropHandler(tree, OnDrop);
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TreeView tree || !e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed || RowAt(e.Source) is not { } row)
            return;
        _pressed = e;
        _pressedAt = e.GetPosition(tree);
        _pressedItem = row.DataContext;
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TreeView tree || _pressed is not { } pressed || _pressedItem is not { } item)
            return;
        if (!e.GetCurrentPoint(tree).Properties.IsLeftButtonPressed)
        {
            Clear();
            return;
        }
        var delta = e.GetPosition(tree) - _pressedAt;
        if (Math.Abs(delta.X) < StartDistance && Math.Abs(delta.Y) < StartDistance)
            return;

        Clear(); // one drag per press
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RowFormat, item));
        await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        Highlight(null);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e) => Clear();

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var allowed = Allowed(sender, e, out _, out _);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(allowed ? RowAt(e.Source) : null);
        e.Handled = true;
    }

    private static void OnDragLeave(object? sender, DragEventArgs e) => Highlight(null);

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        Highlight(null);
        if (Allowed(sender, e, out var source, out var target))
            GetHandler((TreeView)sender!)!.Drop(source!, target);
        e.Handled = true;
    }

    private static bool Allowed(object? sender, DragEventArgs e, out object? source, out object? target)
    {
        source = e.DataTransfer.TryGetValue(RowFormat);
        target = RowAt(e.Source)?.DataContext;
        return sender is TreeView tree && GetHandler(tree) is { } handler && source is not null
               && !ReferenceEquals(source, target) && handler.CanDrop(source, target);
    }

    private static TreeViewItem? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);

    private static void Highlight(TreeViewItem? row)
    {
        if (ReferenceEquals(row, _highlighted))
            return;
        _highlighted?.Classes.Remove("dropTarget");
        _highlighted = row;
        _highlighted?.Classes.Add("dropTarget");
    }

    private static void Clear()
    {
        _pressed = null;
        _pressedItem = null;
    }
}
