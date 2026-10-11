using DevHub.Controls;

namespace DevHub.ViewModels.Documents;

public class TemplatesViewModel : DocumentViewModelBase
{
    public override string Title => "Templates";
    public override string Icon => IconKind.Template;
    public override DocumentNavigation Navigation => new() { Menu = "File", MenuOrder = 100, WelcomeOrder = 30 };

    public string Description =>
        "Create and edit your templates (coming in M5). To browse and use them, open New Project (Ctrl+N).";
}
