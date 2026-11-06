using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

[assembly: AssemblyTitle("EasyIP")]
[assembly: AssemblyProduct("EasyIP")]
[assembly: AssemblyDescription("Switch a network adapter between static IP and DHCP")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace EasyIP
{
    public static class Program
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SwRestore = 9;

        [STAThread]
        public static int Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "EasyIP.SingleInstance", out first))
            {
                if (!first)
                {
                    ActivateExisting();
                    return 0;
                }

                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("unhandled: " + e.ExceptionObject);
                TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("task: " + e.Exception); e.SetObserved(); };

                var app = new Application();
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Write("ui: " + e.Exception);
                    MessageBox.Show("Something went wrong, but EasyIP is still running.\n\n" + e.Exception.Message,
                        "EasyIP", MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };

                try
                {
                    return app.Run(new MainView().Window);
                }
                catch (Exception ex)
                {
                    Log.Write("startup: " + ex);
                    MessageBox.Show("EasyIP couldn't start.\n\n" + ex.Message, "EasyIP",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }

        static void ActivateExisting()
        {
            Process me = Process.GetCurrentProcess();
            Process other = Process.GetProcessesByName(me.ProcessName)
                .FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
            if (other == null) return;
            ShowWindow(other.MainWindowHandle, SwRestore);
            SetForegroundWindow(other.MainWindowHandle);
        }
    }
}
