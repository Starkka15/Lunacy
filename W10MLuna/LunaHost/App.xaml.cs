using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LunaHost
{
    sealed partial class App : Application
    {
        public App() { InitializeComponent(); }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage), e.Arguments);
            // A Start tile of a webOS app (Software Manager's Pin): that app opens.
            LunaKit.LunaShell.Activated(e.Arguments);
            Window.Current.Activate();
        }

        /// A file Windows opened Lunacy with (the manifest's file types): the shell opens it in a
        /// webOS app, starting first if it isn't running.
        protected override void OnFileActivated(FileActivatedEventArgs args)
        {
            LunaKit.LunaShell.FilesActivated(args.Files);
            var frame = Window.Current.Content as Frame;
            if (frame == null) { frame = new Frame(); Window.Current.Content = frame; }
            if (frame.Content == null) frame.Navigate(typeof(MainPage), "");
            Window.Current.Activate();
        }

        /// The in-process background tasks (LunaKit's Background.cs): a timer or network wake.
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            LunaKit.Background.Activated(args.TaskInstance);
        }
    }
}
