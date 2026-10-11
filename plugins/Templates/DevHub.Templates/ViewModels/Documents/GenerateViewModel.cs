using DevHub.Controls;

namespace DevHub.ViewModels.Documents;

public class GenerateViewModel : DocumentViewModelBase
{
    public override string Title => "New Project";
    public override string Icon => IconKind.Add;
    public override DocumentNavigation Navigation => new() { Text = "New Project…", Gesture = "Ctrl+N", Menu = "File", MenuOrder = 0, WelcomeOrder = 10 };

    public string Description =>
        "Pick a template, fill in variables, and stamp out a new project or file. (Coming in M4.)";
}
