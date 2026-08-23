using System;
using System.IO;
using System.Windows;
using MizEdit.Core;

namespace MizEdit
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            UILocalization.Initialize();

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
