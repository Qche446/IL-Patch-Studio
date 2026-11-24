using System.Windows;

namespace ILPatchStudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = SettingsStore.Load(out string? loadError);
        Ui.Configure(settings.Language);
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
        string? path = e.Args.Length > 0 ? e.Args[0] : settings.RestoreLastSession ? settings.LastAssembly : null;
        if (!string.IsNullOrWhiteSpace(path))
            await window.OpenAssemblyAsync(path);
        if (loadError != null)
            window.ShowSettingsLoadError(loadError);
    }
}
