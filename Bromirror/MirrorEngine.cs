using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace Bromirror;

public enum EngineState { Offline, Starting, Ready, Connected, Restarting, Error }

/// <summary>
/// Steuert uxplay.exe: Start/Stopp, Watchdog mit Auto-Neustart, Verbindungserkennung.
/// Alle oeffentlichen Member werden nur auf dem UI-Thread benutzt.
/// </summary>
public sealed class MirrorEngine : IDisposable
{
    public const int BasePort = 47000;
    const string MsysBin = @"C:\msys64\ucrt64\bin";
    const string GstPlugins = @"C:\msys64\ucrt64\lib\gstreamer-1.0";
    static readonly Regex ConnectionRequest =
        new(@"connection request from (.+?) \((.+?)\) with deviceID", RegexOptions.Compiled);

    readonly Dispatcher _ui = Application.Current.Dispatcher;
    readonly DispatcherTimer _poll;
    readonly Queue<DateTime> _recentExits = new();
    Process? _proc;
    DateTime _launchedAt;
    bool _wantRunning;
    int _clientSeenPolls;

    public Settings Settings { get; }
    public string RootDir { get; }
    public string UxPlayExe => Path.Combine(RootDir, "UxPlay", "build", "uxplay.exe");

    public EngineState State { get; private set; } = EngineState.Offline;
    public string? ErrorText { get; private set; }
    public DateTime? RunningSince { get; private set; }
    public int Restarts { get; private set; }
    public int Sessions { get; private set; }
    public string? DeviceName { get; private set; }
    public string? DeviceModel { get; private set; }
    public string? DeviceIp { get; private set; }
    public bool PigeonCastRunning { get; private set; }

    public event Action? Changed;
    public event Action<string>? LogLine;

    public MirrorEngine(Settings settings)
    {
        Settings = settings;
        RootDir = FindRoot();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    public bool IsRunning => State is not (EngineState.Offline or EngineState.Error);

    public void Start()
    {
        if (_wantRunning) return;
        _wantRunning = true;
        _recentExits.Clear();
        ErrorText = null;
        RunningSince = DateTime.Now;
        Launch();
    }

    public void Stop()
    {
        _wantRunning = false;
        KillProcess();
        ClearDevice();
        RunningSince = null;
        SetState(EngineState.Offline);
        Emit("Server gestoppt");
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public static int KillPigeonCast()
    {
        int killed = 0;
        foreach (var p in Process.GetProcessesByName("pigeoncast"))
        {
            using (p)
            {
                try { p.Kill(true); killed++; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return killed;
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "UxPlay", "build", "uxplay.exe")))
                return dir.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MirrorCast");
    }

    void Launch()
    {
        if (!File.Exists(UxPlayExe)) { Fail($"uxplay.exe nicht gefunden: {UxPlayExe}"); return; }
        KillStrays();
        SetState(EngineState.Starting);

        var psi = new ProcessStartInfo(UxPlayExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RootDir,
        };
        foreach (var arg in BuildArgs()) psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = MsysBin + ";" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["GST_PLUGIN_PATH"] = GstPlugins;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) _ui.BeginInvoke(() => OnOutput(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) _ui.BeginInvoke(() => OnOutput(e.Data)); };
        p.Exited += (_, _) => _ui.BeginInvoke(() => OnExited(p));
        try
        {
            p.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            p.Dispose();
            Fail("UxPlay konnte nicht gestartet werden: " + ex.Message);
            return;
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
        _launchedAt = DateTime.Now;
        Emit($"Server gestartet als \u201E{Settings.Name}\u201C (PID {p.Id}, Port {BasePort})");
    }

    IEnumerable<string> BuildArgs()
    {
        yield return "-n"; yield return Settings.Name;
        yield return "-nh";
        yield return "-nohold";
        yield return "-key"; yield return Path.Combine(RootDir, "mirrorcast.pem");
        yield return "-vs"; yield return "d3d11videosink";
        yield return "-reset"; yield return "10";
        yield return "-p"; yield return BasePort.ToString();
        if (Settings.Fullscreen) yield return "-fs";
    }

    void OnOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Emit(line.TrimEnd());

        var m = ConnectionRequest.Match(line);
        if (m.Success)
        {
            DeviceName = m.Groups[1].Value;
            DeviceModel = m.Groups[2].Value;
            Changed?.Invoke();
        }
        if (State == EngineState.Starting && line.Contains("Initialized server socket"))
            SetState(EngineState.Ready);
    }

    void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _proc)) return; // absichtlich beendet
        int code = -1;
        try { code = p.ExitCode; } catch (InvalidOperationException) { }
        _proc = null;
        p.Dispose();
        ClearDevice();
        if (!_wantRunning) return;

        var now = DateTime.Now;
        _recentExits.Enqueue(now);
        while (_recentExits.Count > 0 && now - _recentExits.Peek() > TimeSpan.FromSeconds(60))
            _recentExits.Dequeue();
        if (_recentExits.Count >= 5)
        {
            Fail($"UxPlay stürzt wiederholt ab (Code {code}). Details im Protokoll.");
            return;
        }

        Restarts++;
        Emit($"UxPlay beendet (Code {code}) \u2013 automatischer Neustart in 2 s");
        SetState(EngineState.Restarting);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_wantRunning && _proc == null) Launch();
        };
        t.Start();
    }

    void Poll()
    {
        var pigeons = Process.GetProcessesByName("pigeoncast");
        PigeonCastRunning = pigeons.Length > 0;
        foreach (var p in pigeons) p.Dispose();

        if (_proc != null)
        {
            if (State == EngineState.Starting && DateTime.Now - _launchedAt > TimeSpan.FromSeconds(4))
                SetState(EngineState.Ready);

            var client = FindClient();
            // Zwei Polls in Folge, damit kurze /info-Abfragen des iPhones nicht als Sitzung zaehlen.
            _clientSeenPolls = client != null ? _clientSeenPolls + 1 : 0;

            if (client != null && _clientSeenPolls >= 2 && State == EngineState.Ready)
            {
                DeviceIp = client.ToString();
                Sessions++;
                SetState(EngineState.Connected);
                Emit($"iPhone verbunden ({DeviceIp})");
            }
            else if (client == null && State == EngineState.Connected)
            {
                Emit("Spiegelung beendet");
                ClearDevice();
                SetState(EngineState.Ready);
            }
        }
        Changed?.Invoke();
    }

    static IPAddress? FindClient()
    {
        foreach (var c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            int port = c.LocalEndPoint.Port;
            if (c.State != TcpState.Established || port < BasePort || port > BasePort + 2) continue;
            var remote = c.RemoteEndPoint.Address;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (!IPAddress.IsLoopback(remote)) return remote;
        }
        return null;
    }

    void Fail(string message)
    {
        _wantRunning = false;
        KillProcess();
        ClearDevice();
        RunningSince = null;
        ErrorText = message;
        Emit("FEHLER: " + message);
        SetState(EngineState.Error);
    }

    void ClearDevice()
    {
        DeviceName = DeviceModel = DeviceIp = null;
        _clientSeenPolls = 0;
    }

    void KillProcess()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    static void KillStrays()
    {
        foreach (var p in Process.GetProcessesByName("uxplay"))
        {
            using (p)
            {
                try { p.Kill(true); p.WaitForExit(2000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    void SetState(EngineState s)
    {
        if (State == s) return;
        State = s;
        Changed?.Invoke();
    }

    void Emit(string line)
    {
        LogLine?.Invoke(line);
        try
        {
            var dir = Path.Combine(RootDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"bromirror-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch (IOException) { /* Log-Datei gesperrt: UI-Log reicht */ }
    }

    public void Dispose()
    {
        _poll.Stop();
        _wantRunning = false;
        KillProcess();
    }
}
