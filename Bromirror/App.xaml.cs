using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Bromirror;

public partial class App : Application
{
    Mutex? _instanceLock;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceLock = new Mutex(true, @"Local\Bromirror.App", out bool firstInstance);
        if (!firstInstance)
        {
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        // Die App soll sich nie wieder "einfach schliessen": Fehler loggen statt abstuerzen.
        DispatcherUnhandledException += OnUnhandledException;

        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized");
        string? snapshot = ArgAfter(e.Args, "--snapshot");
        string? introSnapshot = ArgAfter(e.Args, "--intro-snapshot");

        var window = new MainWindow(minimized, snapshot, startTv: e.Args.Contains("--tv"));
        MainWindow = window;

        bool playIntro = introSnapshot != null || (!minimized && snapshot == null && Settings.Load().ShowIntro);
        if (!playIntro)
        {
            window.Show();
            return;
        }

        var intro = new IntroWindow(introSnapshot);
        intro.Finished += () =>
        {
            if (introSnapshot != null) Shutdown();
            else window.ShowWithFade();
        };
        intro.Show();
    }

    static string? ArgAfter(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void ActivateRunningInstance()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            using (p)
            {
                if (p.Id == current.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
            }
        }
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "..", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"bromirror-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss}  UI-FEHLER: {e.Exception}{Environment.NewLine}");
        }
        catch (IOException) { }
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
