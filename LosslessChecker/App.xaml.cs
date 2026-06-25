using System.Windows;

namespace LosslessChecker;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new System.Uri("Themes/Dark.xaml", System.UriKind.Relative)
        });
    }
}
