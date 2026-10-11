using DevHub.Controls;

namespace DevHub.ViewModels.Documents;

public class TemplatesViewModel : DocumentViewModelBase
{
    public override string Title => "Templates";
    public override string Icon => IconKind.Template;
    public override DocumentNavigation Navigation => new() { Menu = "File", MenuOrder = 100, WelcomeOrder = 30 };

    public string Description =>
        "Browse your project and file templates. (Coming in M1 / M4.)";
}
