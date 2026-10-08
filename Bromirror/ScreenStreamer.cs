using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DrawingEncoder = System.Drawing.Imaging.Encoder;

namespace Bromirror;

public sealed record MonitorInfo(int Index, string Label, Rectangle Bounds, bool Primary);

/// <summary>Bildschirme in physischen Pixeln; Index 0 ist immer der Hauptbildschirm.</summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    const uint MONITORINFOF_PRIMARY = 1;

    public static List<MonitorInfo> All()
    {
        var found = new List<(Rectangle Bounds, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var r = info.rcMonitor;
                found.Add((Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return found
            .OrderByDescending(m => m.Primary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select((m, i) => new MonitorInfo(i,
                $"{i + 1}{(m.Primary ? " · Haupt" : "")}  {m.Bounds.Width}×{m.Bounds.Height}", m.Bounds, m.Primary))
            .ToList();
    }

    public static MonitorInfo Get(int index)
    {
        var all = All();
        return index >= 0 && index < all.Count ? all[index] : all[0];
    }
}

/// <summary>
/// "PC → TV": nimmt einen Bildschirm auf und liefert ihn als MJPEG-Livestream per HTTP aus.
/// Jeder Smart-TV-Browser kann das anzeigen – ohne App auf dem Fernseher.
/// </summary>
public sealed class ScreenStreamer : IDisposable
{
    static readonly int[] PortCandidates = { 80, 47100, 8090 };
    const string Boundary = "bromirrorframe";
    const int IdlePreviewFps = 2;
    static readonly byte[] PartEnd = Encoding.ASCII.GetBytes($"\r\n--{Boundary}\r\n");

    readonly object _frameLock = new();
    byte[]? _frame;
    long _frameVersion;
    TaskCompletionSource _nextFrame = NewSignal();
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    Thread? _captureThread;
    int _viewers;
    long _bytesSent;
    int _framesStreamed;

    // Werden pro Bild neu gelesen -> Aenderungen greifen sofort, ohne Neustart.
    public int MonitorIndex { get; set; }
    public int TargetHeight { get; set; } = 1080;
    public int Fps { get; set; } = 24;
    public bool ShowCursor { get; set; } = true;

    public bool IsRunning => _cts != null;
    public int Port { get; private set; }
    public string? Url { get; private set; }
    public int Viewers => Volatile.Read(ref _viewers);

    /// <summary>Wird auf Hintergrund-Threads ausgeloest – der Aufrufer muss selbst auf den UI-Thread wechseln.</summary>
    public event Action<string>? Log;

    public byte[]? LatestFrame { get { lock (_frameLock) return _frame; } }

    /// <summary>Gesendete Bilder und Bytes seit dem letzten Aufruf.</summary>
    public (int Frames, long Bytes) TakeStats() =>
        (Interlocked.Exchange(ref _framesStreamed, 0), Interlocked.Exchange(ref _bytesSent, 0));

    public void Start()
    {
        if (IsRunning) return;
        SocketException? lastError = null;
        foreach (var port in PortCandidates)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (SocketException ex) { lastError = ex; }
        }
        if (_listener == null)
            throw new InvalidOperationException("Kein freier Port gefunden: " + lastError?.Message);

        _cts = new CancellationTokenSource();
        string ip = LanAddress();
        Url = Port == 80 ? $"http://{ip}" : $"http://{ip}:{Port}";
        _ = AcceptLoop(_listener, _cts.Token);
        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "Bromirror PC->TV capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();
        Log?.Invoke($"Sender läuft auf {Url}");
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _captureThread?.Join(1500);
        _captureThread = null;
        TaskCompletionSource waiting;
        lock (_frameLock)
        {
            _frame = null;
            waiting = _nextFrame;
            _nextFrame = NewSignal();
        }
        waiting.TrySetResult(); // wartende Zuschauer aufwecken, damit sie das Abbruch-Token sehen
        cts.Dispose();
        Url = null;
        Log?.Invoke("Sender gestoppt");
    }

    public void Dispose() => Stop();

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------- Aufnahme ----------

    void CaptureLoop(CancellationToken ct)
    {
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality720 = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(DrawingEncoder.Quality, 80L) } };
        using var quality1080 = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(DrawingEncoder.Quality, 72L) } };
        using var buffer = new MemoryStream();
        Bitmap? full = null, scaled = null;
        bool failing = false;
        var clock = Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            long started = clock.ElapsedMilliseconds;
            bool watched = Viewers > 0;
            int fps = watched ? Math.Clamp(Fps, 5, 60) : IdlePreviewFps; // ohne Zuschauer nur Vorschau
            try
            {
                var bounds = Monitors.Get(MonitorIndex).Bounds;
                if (full == null || full.Width != bounds.Width || full.Height != bounds.Height)
                {
                    full?.Dispose();
                    full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                }
                using (var g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    if (ShowCursor) DrawCursor(g, bounds);
                }

                int height = Math.Min(TargetHeight, bounds.Height);
                Bitmap output = full;
                if (height != bounds.Height)
                {
                    int width = (int)Math.Round(bounds.Width * (double)height / bounds.Height / 2) * 2;
                    if (scaled == null || scaled.Width != width || scaled.Height != height)
                    {
                        scaled?.Dispose();
                        scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    }
                    using var g = Graphics.FromImage(scaled);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.DrawImage(full, new Rectangle(0, 0, width, height));
                    output = scaled;
                }

                buffer.SetLength(0);
                output.Save(buffer, jpeg, height <= 720 ? quality720 : quality1080);
                PublishFrame(buffer.ToArray());

                if (failing) { failing = false; Log?.Invoke("Aufnahme läuft wieder"); }
            }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or Win32Exception)
            {
                // z. B. Sperrbildschirm oder UAC-Dialog: Windows verweigert dann die Aufnahme.
                if (!failing) { failing = true; Log?.Invoke("Aufnahme pausiert: " + ex.Message); }
                ct.WaitHandle.WaitOne(500);
                continue;
            }

            int wait = (int)(1000 / fps - (clock.ElapsedMilliseconds - started));
            if (wait > 0) ct.WaitHandle.WaitOne(wait);
        }
        full?.Dispose();
        scaled?.Dispose();
    }

    void PublishFrame(byte[] jpg)
    {
        TaskCompletionSource previous;
        lock (_frameLock)
        {
            _frame = jpg;
            _frameVersion++;
            previous = _nextFrame;
            _nextFrame = NewSignal();
        }
        previous.TrySetResult();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    const int CURSOR_SHOWING = 1;
    const int DI_NORMAL = 3;

    static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0) return;
        if (!bounds.Contains(info.pt.X, info.pt.Y)) return;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, info.pt.X - bounds.Left - hotX, info.pt.Y - bounds.Top - hotY, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    // ---------- Minimaler HTTP-Server (TcpListener braucht im Gegensatz zu HttpListener keine Adminrechte) ----------

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => HandleClient(client, ct));
        }
    }

    async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            client.SendTimeout = 5000;
            var stream = client.GetStream();
            string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
            try
            {
                string? path = await ReadRequestPath(stream, ct);
                if (path == null) return;
                if (path.StartsWith("/stream")) await ServeStream(stream, remote, ct);
                else if (path.StartsWith("/snapshot")) await ServeSnapshot(stream, ct);
                else if (path == "/" || path.StartsWith("/?") || path.StartsWith("/index"))
                    await WriteResponse(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ViewerPage), ct);
                else
                    await WriteResponse(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("404"), ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Zuschauer hat getrennt oder der Sender wurde gestoppt.
            }
        }
    }

    static async Task<string?> ReadRequestPath(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        int length = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (length < buf.Length)
        {
            int n = await stream.ReadAsync(buf.AsMemory(length), timeout.Token);
            if (n == 0) return null;
            length += n;
            if (Encoding.ASCII.GetString(buf, 0, length).Contains("\r\n\r\n")) break;
        }
        var requestLine = Encoding.ASCII.GetString(buf, 0, length).Split("\r\n")[0].Split(' ');
        return requestLine.Length >= 2 && requestLine[0] == "GET" ? requestLine[1] : null;
    }

    static async Task WriteResponse(NetworkStream stream, string status, string type, byte[] body, CancellationToken ct)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    async Task ServeSnapshot(NetworkStream stream, CancellationToken ct)
    {
        var frame = LatestFrame;
        if (frame == null) await WriteResponse(stream, "503 Service Unavailable", "text/plain", Encoding.ASCII.GetBytes("noch kein Bild"), ct);
        else await WriteResponse(stream, "200 OK", "image/jpeg", frame, ct);
    }

    async Task ServeStream(NetworkStream stream, string remote, CancellationToken ct)
    {
        // Jeder Teil endet direkt mit der naechsten Boundary -> Browser zeigt das Bild sofort, nicht erst beim naechsten.
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n" +
            $"Cache-Control: no-cache, no-store\r\nPragma: no-cache\r\nConnection: close\r\n\r\n--{Boundary}\r\n"), ct);

        Interlocked.Increment(ref _viewers);
        Log?.Invoke($"Zuschauer verbunden: {remote}");
        try
        {
            long sentVersion = -1;
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                long version;
                Task next;
                lock (_frameLock)
                {
                    frame = _frame;
                    version = _frameVersion;
                    next = _nextFrame.Task;
                }
                if (frame == null || version == sentVersion)
                {
                    await Task.WhenAny(next, Task.Delay(2000, ct));
                    continue;
                }
                var head = Encoding.ASCII.GetBytes($"Content-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
                await stream.WriteAsync(head, ct);
                await stream.WriteAsync(frame, ct);
                await stream.WriteAsync(PartEnd, ct);
                Interlocked.Add(ref _bytesSent, head.Length + frame.Length + PartEnd.Length);
                Interlocked.Increment(ref _framesStreamed);
                sentVersion = version;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _viewers);
            Log?.Invoke($"Zuschauer getrennt: {remote}");
        }
    }

    static string LanAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            if (!hasGateway) continue;
            var v4 = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (v4 != null) return v4.Address.ToString();
        }
        return "localhost";
    }

    // ES5 + keine Abhaengigkeiten, damit auch alte TV-Browser mitspielen.
    const string ViewerPage = """
<!doctype html>
<html lang="de"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Bromirror · PC → TV</title>
<style>
html,body{margin:0;height:100%;background:#000;overflow:hidden;cursor:none}
img{width:100%;height:100%;object-fit:contain;display:block}
#hint{position:fixed;left:50%;bottom:24px;transform:translateX(-50%);padding:10px 20px;border-radius:12px;
background:rgba(10,10,12,.82);border:1px solid rgba(255,122,26,.5);color:#ffb067;
font:600 18px "Segoe UI",sans-serif;transition:opacity 1.2s}
</style></head>
<body>
<img id="v" alt="">
<div id="hint">Bromirror · PC → TV &nbsp;—&nbsp; OK / Klick = Vollbild</div>
<script>
var v=document.getElementById('v');
function connect(){v.src='/stream?t='+new Date().getTime();}
v.onerror=function(){setTimeout(connect,1500);};
connect();
document.body.onclick=function(){
  var e=document.documentElement;
  var fs=e.requestFullscreen||e.webkitRequestFullscreen||e.msRequestFullscreen;
  if(fs){fs.call(e);}
};
setTimeout(function(){document.getElementById('hint').style.opacity=0;},5000);
</script>
</body></html>
""";
}
