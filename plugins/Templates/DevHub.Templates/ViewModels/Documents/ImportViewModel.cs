using DevHub.Controls;

namespace DevHub.ViewModels.Documents;

public class ImportViewModel : DocumentViewModelBase
{
    public override string Title => "Import";
    public override string Icon => IconKind.Import;
    public override DocumentNavigation Navigation => new() { Text = "Import Folder…", Menu = "File", MenuOrder = 1, WelcomeOrder = 20 };

    public string Description =>
        "Turn an existing folder into a reusable template. (Coming in M7.)";
}
