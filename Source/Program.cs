using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace FloatTranslate
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatTranslate");
            try
            {
                if (args.Length >= 2 && args[0] == "--self-test") return TestRunner.Run(args[1]);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml")) app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
                if (args.Length >= 2 && args[0] == "--ui-test")
                {
                    int result = 1;
                    app.Dispatcher.BeginInvoke(new Action(async delegate { result = await TestRunner.RunUiTest(args[1]); app.Shutdown(result); }));
                    app.Run(); return result;
                }
                if (args.Length >= 2 && args[0] == "--preview")
                {
                    using (var preview = new DesktopController(new Store(Path.Combine(args[1], "unused-preview-data")), true)) preview.RenderPreviews(args[1]);
                    return 0;
                }
                bool created;
                using (var instance = new Mutex(true, @"Local\FloatTranslate.Instance", out created))
                using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FloatTranslate.Show"))
                {
                    if (!created) { wake.Set(); return 0; }
                    var store = new Store(dataPath);
                    using (var controller = new DesktopController(store, false))
                    {
                        app.MainWindow = controller.Window;
                        app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                        {
                            e.Handled = true; MessageBox.Show("操作失败：" + e.Exception.Message, "浮译");
                        };
                        ThreadPool.RegisterWaitForSingleObject(wake, delegate(object state, bool timedOut) { if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(controller.ShowSettings)); }, null, -1, false);
                        if ((args.Length > 0 && args[0] == "--background") || (store.Current.IsConfigured && !(args.Length > 0 && args[0] == "--settings")))
                        {
                            controller.Window.ShowActivated = false; controller.Window.ShowInTaskbar = false; controller.Window.Opacity = 0;
                            controller.Window.Show(); controller.Window.Hide(); controller.Window.Opacity = 1; controller.Window.ShowInTaskbar = true; controller.Window.ShowActivated = true;
                        }
                        else controller.ShowSettings();
                        app.Run();
                    }
                    instance.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception error)
            {
                try { Directory.CreateDirectory(dataPath); File.WriteAllText(Path.Combine(dataPath, "startup-error.txt"), error.ToString()); } catch { }
                if (args.Length > 0) { try { File.WriteAllText(args[args.Length - 1] + ".error.txt", error.ToString()); } catch { } }
                else MessageBox.Show("浮译无法启动：" + error.Message, "浮译");
                return 1;
            }
        }
    }
}
