using System.Windows;
using Mcdl.App.ViewModels;

namespace Mcdl.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        _ = vm.InitializeAsync();
    }
}
