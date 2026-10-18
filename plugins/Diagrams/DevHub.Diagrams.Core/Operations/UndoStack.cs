namespace DevHub.Diagrams.Core;

/// <summary>
/// Runs operations on a diagram and keeps their undos: <see cref="Do"/>, <see cref="Undo"/>, <see cref="Redo"/>.
/// Knows whether the diagram changed since it was last saved (<see cref="IsDirty"/>), also after undoing back to it.
/// </summary>
public sealed class UndoStack(Diagram diagram, int limit = 500)
{
    private readonly List<(IDiagramOperation Undo, IDiagramOperation Redo)> _done = [];
    private readonly Stack<(IDiagramOperation Undo, IDiagramOperation Redo)> _undone = new();
    private int _savedAt;
    private bool _savedLost;

    public Diagram Diagram { get; } = diagram;

    /// <summary>Something changed (an operation done, undone or redone, or the diagram saved).</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _done.Count > 0;
    public bool CanRedo => _undone.Count > 0;

    /// <summary>"Move" for the next Undo, or null.</summary>
    public string? UndoName => _done.Count > 0 ? _done[^1].Redo.Name : null;

    public string? RedoName => _undone.Count > 0 ? _undone.Peek().Redo.Name : null;

    public bool IsDirty => _savedLost || _savedAt != _done.Count;

    /// <exception cref="DiagramRuleException">Not allowed; the diagram is unchanged.</exception>
    public void Do(IDiagramOperation operation)
    {
        var undo = operation.Apply(Diagram);
        _done.Add((undo, operation));
        if (_undone.Count > 0 && _savedAt > _done.Count - 1)
            _savedLost = true;   // the saved state was in the redo branch that's now gone
        _undone.Clear();
        if (_done.Count > limit)
        {
            _done.RemoveAt(0);
            _savedAt--;
            if (_savedAt < 0)
                _savedLost = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_done.Count == 0)
            return;
        var step = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        var redo = step.Undo.Apply(Diagram);
        _undone.Push((step.Undo, redo));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_undone.Count == 0)
            return;
        var step = _undone.Pop();
        var undo = step.Redo.Apply(Diagram);
        _done.Add((undo, step.Redo));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The diagram was saved as it is now.</summary>
    public void MarkSaved()
    {
        _savedAt = _done.Count;
        _savedLost = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
