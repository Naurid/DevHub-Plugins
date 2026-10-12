using System.Collections.ObjectModel;
using DevHub.Sdk.Core.Formatting;

namespace DevHub.ViewModels.Git;

/// <summary>
/// Turns a list of changed files into the rows a file list shows: the files as they are (flat),
/// or folders and files in tree order (Fork's tree view; folded folders hide what's inside).
/// Shared by Changes and the commit / stash details.
/// </summary>
public static class ChangeRows
{
    /// <param name="collapsed">Folded folders (by path); kept by the caller across rebuilds.</param>
    /// <param name="toggle">What a folder row's arrow / double-click does.</param>
    public static void Build(IReadOnlyList<FileChangeViewModel> files, ObservableCollection<ChangeRowViewModel> rows,
        bool asTree, HashSet<string> collapsed, Action<ChangeFolderRowViewModel> toggle)
    {
        rows.Clear();
        if (!asTree)
        {
            foreach (var file in files)
            {
                file.Depth = 0;
                file.ShowDirectory = true;
                rows.Add(file);
            }
            return;
        }

        // Folders first, single-child folder chains merged ("src/DevHub.App").
        Add(PathTree.Build(files, f => f.Change.Path, compress: true), 0);

        void Add(IReadOnlyList<PathTreeNode<FileChangeViewModel>> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                if (node.IsFolder)
                {
                    var inside = PathTree.Flatten(node.Children).Where(n => !n.IsFolder).Select(n => n.Item!).ToList();
                    var folder = new ChangeFolderRowViewModel(node.Name, node.Path, inside, !collapsed.Contains(node.Path), toggle)
                    {
                        Depth = depth,
                    };
                    rows.Add(folder);
                    if (folder.IsExpanded)
                        Add(node.Children, depth + 1);
                }
                else
                {
                    var file = node.Item!;
                    file.Depth = depth;
                    file.ShowDirectory = false;
                    rows.Add(file);
                }
            }
        }
    }

    /// <summary>Folds or unfolds <paramref name="folder"/> in <paramref name="collapsed"/>.</summary>
    public static void Toggle(ChangeFolderRowViewModel folder, HashSet<string> collapsed)
    {
        if (!collapsed.Add(folder.Path))
            collapsed.Remove(folder.Path);
    }
}
