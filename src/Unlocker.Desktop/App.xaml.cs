using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Unlocker.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        StartupDiagnostics.Record("App constructor started");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupDiagnostics.Record("Unhandled AppDomain exception: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            StartupDiagnostics.Record("Unobserved task exception: " + e.Exception);
        UnhandledException += (_, e) =>
            StartupDiagnostics.Record("Unhandled WinUI exception: " + e.Exception);
        try
        {
            InitializeComponent();
            StartupDiagnostics.Record("App.InitializeComponent completed");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Record("App.InitializeComponent failed: " + ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Record("OnLaunched started");
        try
        {
            _window = new MainWindow();
            StartupDiagnostics.Record("MainWindow constructed");
            _window.Activate();
            StartupDiagnostics.Record("MainWindow activated");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Record("OnLaunched failed: " + ex);
            throw;
        }
    }
}

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();

    internal static void Record(string message)
    {
        // Diagnostics must never cause an additional startup failure.
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleUnlockerNext", "Logs");
            Directory.CreateDirectory(directory);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(directory, "startup.log"),
                    $"{DateTimeOffset.Now:O} | PID {Environment.ProcessId} | {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // A read-only or restricted profile must not prevent the UI from opening.
        }
    }
}
