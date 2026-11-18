using System.IO;
using LunaKit;
using Windows.ApplicationModel;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LunaHost
{
    public sealed partial class MainPage : Page
    {
        private LunaCard card;

        public MainPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (card != null) return;
            // assemble-assets.ps1 names the bundled app in webos\app-id.txt.
            var webos = Path.Combine(Package.Current.InstalledLocation.Path, "webos");
            var appId = File.ReadAllText(Path.Combine(webos, "app-id.txt")).Trim();
            card = new LunaCard(Card, appId, Path.Combine(webos, "apps", appId), webos);
            card.Start();
        }
    }
}
