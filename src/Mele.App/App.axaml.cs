using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mele.App.Services;
using Mele.App.ViewModels;
using Mele.App.Views;

namespace Mele.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(new AppBackend(), ui: a => Dispatcher.UIThread.Post(a)) };
        base.OnFrameworkInitializationCompleted();
    }
}
