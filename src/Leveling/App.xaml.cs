using Microsoft.UI.Xaml;
using Multiverse.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Multiverse
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            StartupDiagnostics.Stage("App.Constructor");
            UnhandledException += App_UnhandledException;
            try
            {
                StartupDiagnostics.Stage("App.InitializeComponent");
                InitializeComponent();
                StartupDiagnostics.Stage("App.ResourcesReady");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("APP_XAML_INIT_ERROR", ex);
                throw;
            }
        }

        private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            if (e.Exception is not null)
                StartupDiagnostics.RecordException("XAML_UNHANDLED", e.Exception);
            else
                StartupDiagnostics.Stage("XAML_UNHANDLED", e.Message);
            // Do not mark the exception handled. Preserve the debugger/Windows crash report.
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                StartupDiagnostics.Stage("MainWindow.Create");
                string[] command = Environment.GetCommandLineArgs();
                int sessionTest = Array.IndexOf(command, "--session-loopback-test");
                if (sessionTest >= 0 && sessionTest + 1 < command.Length)
                {
                    _ = SessionLoopbackSelfTest.RunAsync(command[sessionTest + 1], command.Contains("--host"));
                    return;
                }
                int selfTest = Array.IndexOf(command, "--video-self-test");
                if (selfTest >= 0 && selfTest + 1 < command.Length)
                {
                    int durationIndex = Array.IndexOf(command, "--seconds");
                    int seconds = durationIndex >= 0 && durationIndex + 1 < command.Length && int.TryParse(command[durationIndex + 1], out int parsed) ? parsed : 15;
                    int fpsIndex = Array.IndexOf(command, "--fps");
                    uint fps = fpsIndex >= 0 && fpsIndex + 1 < command.Length && uint.TryParse(command[fpsIndex + 1], out uint parsedFps) ? parsedFps : 60u;
                    _ = VideoLocalSelfTest.RunAsync(command[selfTest + 1], command.Contains("--qhd"), seconds, fps, command.Contains("--exercise-presentation"));
                    return;
                }
                _window = new MainWindow();
                StartupDiagnostics.Stage("MainWindow.Activate");
                _window.Activate();
                StartupDiagnostics.Ready();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("APP_LAUNCH_ERROR", ex);
                throw;
            }
        }
    }
}
