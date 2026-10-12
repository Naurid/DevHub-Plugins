using Avalonia.Threading;
using DevHub.Integration;
using DevHub.Sdk.Core.Formatting;

namespace DevHub.ViewModels.Git;

/// <summary>One mark next to a commit or a branch (from another plugin's <see cref="IGitDecorator"/>, e.g. CI's ✓).</summary>
public sealed class DecorationViewModel(Decoration decoration)
{
    public string IconKind => decoration.IconKind;
    public string ToolTip => decoration.ToolTip;
    public DecorationTone Tone => decoration.Tone;

    /// <summary>The link to open on click: http(s) only, else null (no link at all).</summary>
    public Uri? Url => Markdown.IsSafeLink(decoration.Url) && decoration.Url!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(decoration.Url) : null;

    public bool IsLink => Url is not null;
    public bool IsSuccess => Tone == DecorationTone.Success;
    public bool IsWarning => Tone == DecorationTone.Warning;
    public bool IsError => Tone == DecorationTone.Error;
    public bool IsRunning => Tone == DecorationTone.Running;

    public override string ToString() => ToolTip;
}

/// <summary>
/// The decorations of one repository tab: asks every registered <see cref="IGitDecorator"/> (none: nothing to show)
/// for the commits and branches on screen, merged in decorator order, and tells the tab when a decorator says they
/// changed for this repository. A decorator that throws is skipped. Disposed with the tab (unsubscribes).
/// </summary>
public sealed class GitDecorations : IDisposable
{
    private readonly IReadOnlyList<IGitDecorator> _decorators;
    private readonly string _repositoryPath;

    public GitDecorations(IEnumerable<IGitDecorator> decorators, string repositoryPath)
    {
        _decorators = decorators.ToList();
        _repositoryPath = repositoryPath;
        foreach (var decorator in _decorators)
            decorator.Changed += OnDecoratorChanged;
    }

    /// <summary>Decorations of this repository changed (on the UI thread): ask again for what's shown.</summary>
    public event EventHandler? Changed;

    public bool IsEmpty => _decorators.Count == 0;

    public IReadOnlyDictionary<string, IReadOnlyList<DecorationViewModel>> ForCommits(IReadOnlyList<string> commitShas) =>
        Merge(d => d.DecorateCommits(_repositoryPath, commitShas));

    public IReadOnlyDictionary<string, IReadOnlyList<DecorationViewModel>> ForBranches(IReadOnlyList<string> branches) =>
        Merge(d => d.DecorateBranches(_repositoryPath, branches));

    private Dictionary<string, IReadOnlyList<DecorationViewModel>> Merge(Func<IGitDecorator, IReadOnlyDictionary<string, IReadOnlyList<Decoration>>> ask)
    {
        var merged = new Dictionary<string, List<DecorationViewModel>>(StringComparer.Ordinal);
        foreach (var decorator in _decorators)
        {
            IReadOnlyDictionary<string, IReadOnlyList<Decoration>> found;
            try
            {
                found = ask(decorator);
            }
            catch (Exception)
            {
                continue; // another plugin's problem: no marks from it
            }
            foreach (var (key, decorations) in found)
            {
                if (!merged.TryGetValue(key, out var list))
                    merged[key] = list = [];
                list.AddRange(decorations.Select(d => new DecorationViewModel(d)));
            }
        }
        return merged.ToDictionary(p => p.Key, p => (IReadOnlyList<DecorationViewModel>)p.Value, StringComparer.Ordinal);
    }

    private void OnDecoratorChanged(object? sender, DecorationsChangedEventArgs e)
    {
        if (!string.Equals(Path.GetFullPath(e.RepositoryFolder).TrimEnd('\\', '/'), Path.GetFullPath(_repositoryPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        foreach (var decorator in _decorators)
            decorator.Changed -= OnDecoratorChanged;
    }
}
