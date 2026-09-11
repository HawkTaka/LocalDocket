using System.Windows;
using LocalDocket.Core;

namespace LocalDocket.App;

public partial class App : Application
{
    Mutex? _mutex;
    DocketHost? _host;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\LocalDocket.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("Local Docket is already running (look in the tray).", "Local Docket", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Log.Init(DocketPaths.LogPath);
        DispatcherUnhandledException += (_, ex) => { Log.Error("UI", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Domain", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task", ex.Exception); ex.SetObserved(); };
        // The tray menu is WinForms: without this an exception in a menu handler shows the Continue/Quit dialog and "Quit" kills the tray.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, ex) => Log.Error("Tray", ex.Exception);

        try
        {
            _host = new DocketHost();
            _host.Start();
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            MessageBox.Show("Local Docket could not start:\n\n" + ex.Message + "\n\nSee " + DocketPaths.LogPath, "Local Docket", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        _tray = new TrayIcon(_host);
        Log.Info($"started; inbox={_host.Taxonomy.InboxPath}; taxonomy={_host.Taxonomy.SourcePath}");

        // LocalDocket.exe --scan [folder]   opens Scan mode straight away
        // LocalDocket.exe --search          opens the search window
        // LocalDocket.exe --index           opens the indexing window
        // LocalDocket.exe --chat            opens the chat window
        var a = e.Args;
        if (a.Contains("--scan", StringComparer.OrdinalIgnoreCase))
        {
            var i = Array.FindIndex(a, x => x.Equals("--scan", StringComparison.OrdinalIgnoreCase));
            var folder = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[i + 1] : null;
            new ScanWindow(_host, folder).Show();
        }
        if (a.Contains("--search", StringComparer.OrdinalIgnoreCase)) new SearchWindow(_host).Show();
        if (a.Contains("--index", StringComparer.OrdinalIgnoreCase)) new IndexWindow(_host).Show();
        if (a.Contains("--chat", StringComparer.OrdinalIgnoreCase)) new ChatWindow(_host).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _host?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
