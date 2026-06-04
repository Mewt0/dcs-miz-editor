using System;
using System.IO;
using System.Windows;

namespace MizEdit
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new MainWindow();
            window.Show();

            if (e.Args.Length > 0)
            {
                var missionPath = Path.GetFullPath(e.Args[0]);
                window.Dispatcher.BeginInvoke(new Action(() => window.TryOpenMission(missionPath)));
            }
        }
    }
}
