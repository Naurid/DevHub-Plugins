namespace DevHub.Actions;

/// <summary>Ids of the Git plugin's actions; they act on the active repository (XAML: <c>{a:Action Git.Clone}</c>).</summary>
public static class GitActionIds
{
    public const string Refresh = "Git.Refresh";
    public const string Commit = "Git.Commit";
    public const string Discard = "Git.Discard";
    public const string Branches = "Git.Branches";
    public const string NewBranch = "Git.NewBranch";
    public const string Fetch = "Git.Fetch";
    public const string Pull = "Git.Pull";
    public const string Push = "Git.Push";
    public const string ForcePush = "Git.ForcePush";
    public const string Stash = "Git.Stash";
    public const string Unstash = "Git.Unstash";
    public const string ManageRemotes = "Git.ManageRemotes";
    public const string Clone = "Git.Clone";
    public const string OpenRepository = "Git.OpenRepository";
    public const string OpenTerminal = "Git.OpenTerminal";

    /// <summary>Prefix of the Publish action; the hosting service's name follows ("Git.PublishToGitHub").</summary>
    public const string PublishPrefix = "Git.PublishTo";
}
