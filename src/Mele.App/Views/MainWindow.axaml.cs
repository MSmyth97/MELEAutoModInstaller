using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Mele.App.ViewModels;

namespace Mele.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                // Keep the newest log line in view while a run is going.
                vm.Log.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && vm.Log.Count > 0) LogList.ScrollIntoView(vm.Log[^1]);
                };
        };
    }

    private async void OnBrowseGame(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose the Mass Effect Legendary Edition folder", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path && DataContext is MainViewModel vm) vm.GameDir = path;
    }
}
