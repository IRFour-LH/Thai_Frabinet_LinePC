using System.Windows;
using AoiUploader.Services;

namespace AoiUploader;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, @"Local\AoiUploader_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("AOI Uploader is already running.", "AOI Uploader", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unexpected error: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "AOI Uploader - error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Background error: " + args.Exception.GetBaseException().Message);
            args.SetObserved();
        };

        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
