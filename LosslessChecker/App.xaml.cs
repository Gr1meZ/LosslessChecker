using System.Windows;
using LosslessChecker.ViewModels;

namespace LosslessChecker;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isDark = MainViewModel.LoadThemeSetting();
        var app = Current;
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", System.UriKind.Relative)
        });
    }
}
