using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevHub.ViewModels.ToolWindows;

/// <summary>The sample tool window: a greeting and a counter.</summary>
public sealed partial class HelloToolWindowViewModel : ToolWindowViewModelBase
{
    public override string Id => "hello-world";
    public override string Title => "Hello World";
    public override string IconKind => Controls.IconKind.Info;
    public override ToolWindowSlot Slot => ToolWindowSlot.Right;

    [ObservableProperty] private int _clicks;

    public string Greeting => Clicks == 0 ? "Hello from a marketplace plugin!" : $"Hello again ({Clicks}×).";

    partial void OnClicksChanged(int value) => OnPropertyChanged(nameof(Greeting));

    [RelayCommand]
    private void Wave() => Clicks++;
}
