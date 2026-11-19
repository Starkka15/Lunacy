using System.IO;
using LunaKit;
using Windows.ApplicationModel;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LunaHost
{
    public sealed partial class MainPage : Page
    {
        private LunaShell shell;

        public MainPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (shell != null) return;
            // assemble-assets.ps1 lays out webos\: the apps, Lunacy's layer, the frameworks, the shell.
            shell = new LunaShell(Card, Path.Combine(Package.Current.InstalledLocation.Path, "webos"));
            shell.Start();
        }
    }
}
