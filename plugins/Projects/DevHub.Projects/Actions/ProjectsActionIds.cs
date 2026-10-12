namespace DevHub.Actions;

/// <summary>Ids of the Projects plugin's actions (XAML uses the same strings: <c>{a:Action Repositories.Rescan}</c>).</summary>
public static class ProjectsActionIds
{
    // "Repositories." prefix kept from before the rename to Projects, so saved shortcuts stay valid.
    public const string AddScanFolder = "Repositories.AddFolder";
    public const string RescanRepositories = "Repositories.Rescan";
}
