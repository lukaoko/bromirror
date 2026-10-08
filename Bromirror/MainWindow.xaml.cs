using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Bromirror;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int MaxLogLines = 500;

    readonly Settings _settings = Settings.Load();
    readonly MirrorEngine _engine;
    readonly ObservableCollection<string> _log = new();
    readonly SolidColorBrush _stateBrush = new(Color.FromRgb(0x5A, 0x5E, 0x6B));
    readonly string? _snapshotPath;
    EngineState? _shownState;
    bool _loading = true;

    record StateLook(Color Color, Color Inner, string Title, string Subtitle, bool Pulse);

    readonly bool _startTv;

    public MainWindow(bool startMinimized, string? snapshotPath = null, bool startTv = false)
    {
        InitializeComponent();
        _snapshotPath = snapshotPath;
        _startTv = startTv;
        _engine = new MirrorEngine(_settings);
        _engine.Changed += Refresh;
        _engine.LogLine += AddLog;
        LogList.ItemsSource = _log;

        PulseRing1.Stroke = _stateBrush;
        PulseRing2.Stroke = _stateBrush;
        OuterRing.Stroke = _stateBrush;
        StatusTitle.Foreground = _stateBrush;

        NameBox.Text = _settings.Name;
        StepName.Text = _settings.Name;
        FullscreenToggle.IsChecked = _settings.Fullscreen;
        AutoStartToggle.IsChecked = _settings.AutoStartServer;
        IntroToggle.IsChecked = _settings.ShowIntro;
        WindowsToggle.IsChecked = IsWindowsAutostart();
        FooterInfo.Text = $"UxPlay 1.74  ·  Port {MirrorEngine.BasePort}  ·  v1.1";
        InitTv();
        _loading = false;

        if (startMinimized) WindowState = WindowState.Minimized;

        StartAnimations();
        ApplyState(EngineState.Offline, animate: false);
        Loaded += OnLoaded;
        Closing += (_, _) => _engine.Dispose();
    }

    public void ShowWithFade()
    {
        Opacity = 0;
        Show();
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.AutoStartServer || _snapshotPath != null) _engine.Start();
        Refresh();
        if (_startTv)
        {
            TvTab.IsChecked = true;
            StartTv();
        }

        if (_snapshotPath != null)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) => { t.Stop(); SaveSnapshot(_snapshotPath); Close(); };
            t.Start();
        }
    }

    // ---------- Darstellung ----------

    static StateLook LookFor(EngineState s) => s switch
    {
        EngineState.Starting => new(C("#FF7A1A"), C("#FFB067"), "STARTET …", "Server wird hochgefahren.", true),
        EngineState.Ready => new(C("#FF7A1A"), C("#FFB067"), "BEREIT", "Warte auf dein iPhone – öffne die Bildschirmsynchronisierung.", true),
        EngineState.Connected => new(C("#3DDC97"), C("#9BF2CB"), "VERBUNDEN", "Dein iPhone wird gerade gespiegelt.", true),
        EngineState.Restarting => new(C("#FFB547"), C("#FFD99A"), "NEUSTART", "Kurzer Aussetzer – Bromirror rettet sich gerade selbst.", true),
        EngineState.Error => new(C("#FF5D5D"), C("#FF9C9C"), "FEHLER", "", false),
        _ => new(C("#5A5E6B"), C("#8D91A0"), "OFFLINE", "Server ist gestoppt. Starte ihn, damit dein iPhone Bromirror findet.", false),
    };

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    void ApplyState(EngineState s, bool animate = true)
    {
        var look = LookFor(s);
        var d = animate ? TimeSpan.FromMilliseconds(450) : TimeSpan.Zero;
        Animate(_stateBrush, SolidColorBrush.ColorProperty, look.Color, d);
        Animate(CoreOuter, GradientStop.ColorProperty, look.Color, d);
        Animate(CoreInner, GradientStop.ColorProperty, look.Inner, d);
        Animate(CoreGlow, System.Windows.Media.Effects.DropShadowEffect.ColorProperty, look.Color, d);

        StatusTitle.Text = look.Title;
        StatusSub.Text = s == EngineState.Error ? _engine.ErrorText ?? "Unbekannter Fehler." : look.Subtitle;
        PulseHost.Visibility = look.Pulse ? Visibility.Visible : Visibility.Hidden;
        CoreGlyph.Text = s switch
        {
            EngineState.Connected => "",
            EngineState.Error => "",
            EngineState.Restarting => "",
            EngineState.Offline => "",
            _ => "",
        };
    }

    static void Animate(Animatable target, DependencyProperty prop, Color to, TimeSpan duration)
    {
        if (duration == TimeSpan.Zero) { target.BeginAnimation(prop, null); target.SetValue(prop, to); return; }
        target.BeginAnimation(prop, new ColorAnimation(to, duration) { EasingFunction = new CubicEase() });
    }

    void StartAnimations()
    {
        AnimateRing(PulseRing1, TimeSpan.Zero);
        AnimateRing(PulseRing2, TimeSpan.FromSeconds(1.3));

        var breathe = new DoubleAnimation(38, 64, TimeSpan.FromSeconds(2.2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        CoreGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, breathe);

        var spin = new RotateTransform();
        OuterRing.RenderTransformOrigin = new Point(0.5, 0.5);
        OuterRing.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(40)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    static void AnimateRing(Ellipse ring, TimeSpan delay)
    {
        var scale = new ScaleTransform(1, 1);
        ring.RenderTransformOrigin = new Point(0.5, 0.5);
        ring.RenderTransform = scale;
        var duration = TimeSpan.FromSeconds(2.6);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.85, 1.6, duration) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        var fade = new DoubleAnimation(0.8, 0, duration) { BeginTime = delay, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    void Refresh()
    {
        var s = _engine.State;
        if (_shownState != s)
        {
            ApplyState(s);
            _shownState = s;
        }

        if (s == EngineState.Connected)
        {
            var parts = new[] { _engine.DeviceName, _engine.DeviceModel, _engine.DeviceIp }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            DeviceText.Text = string.Join("  ·  ", parts);
            DevicePanel.Visibility = Visibility.Visible;
        }
        else
        {
            DevicePanel.Visibility = Visibility.Collapsed;
        }

        UptimeText.Text = _engine.RunningSince is DateTime since ? FormatSpan(DateTime.Now - since) : "--:--";
        SessionsText.Text = _engine.Sessions.ToString();
        RestartsText.Text = _engine.Restarts.ToString();
        PowerButton.Content = _engine.IsRunning ? "SERVER STOPPEN" : "SERVER STARTEN";
        DisconnectButton.IsEnabled = s == EngineState.Connected;
        PigeonBanner.Visibility = _engine.PigeonCastRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    void AddLog(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > MaxLogLines) _log.RemoveAt(0);
        if (LogPanel.IsVisible) LogList.ScrollIntoView(_log[^1]);
    }

    // ---------- Aktionen ----------

    void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
        Refresh();
    }

    void Disconnect_Click(object sender, RoutedEventArgs e) => _engine.Restart();

    void KillPigeon_Click(object sender, RoutedEventArgs e)
    {
        int n = MirrorEngine.KillPigeonCast();
        AddLog(n > 0 ? "PigeonCast beendet" : "PigeonCast konnte nicht beendet werden");
        // Freigewordenen mDNS-Port sauber neu belegen.
        if (n > 0 && _engine.IsRunning && _engine.State != EngineState.Connected) _engine.Restart();
    }

    void SaveName_Click(object sender, RoutedEventArgs e) => SaveName();

    void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveName();
    }

    void SaveName()
    {
        var name = NameBox.Text.Replace("\"", "").Trim();
        if (name.Length == 0)
        {
            NameBox.Text = _settings.Name;
            return;
        }
        if (name == _settings.Name) return;

        _settings.Name = name;
        _settings.Save();
        StepName.Text = name;
        AddLog($"Name geändert auf „{name}“");
        if (_engine.IsRunning) _engine.Restart();

        SaveNameButton.Content = "✓ Gespeichert";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        t.Tick += (_, _) => { t.Stop(); SaveNameButton.Content = "Speichern"; };
        t.Start();
    }

    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender == WindowsToggle)
        {
            SetWindowsAutostart(WindowsToggle.IsChecked == true);
            return;
        }

        _settings.Fullscreen = FullscreenToggle.IsChecked == true;
        _settings.AutoStartServer = AutoStartToggle.IsChecked == true;
        _settings.ShowIntro = IntroToggle.IsChecked == true;
        _settings.Save();

        // Vollbild greift beim naechsten Start von UxPlay; laufende Spiegelung nicht abreissen.
        if (sender == FullscreenToggle && _engine.State == EngineState.Ready) _engine.Restart();
    }

    static bool IsWindowsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Bromirror") != null;
    }

    static void SetWindowsAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("Bromirror", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("Bromirror", throwOnMissingValue: false);
    }

    void LogToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LogToggle.Content = show ? "PROTOKOLL AUSBLENDEN  ▴" : "PROTOKOLL ANZEIGEN  ▾";
        if (show && _log.Count > 0) LogList.ScrollIntoView(_log[^1]);
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Selbsttest: Fenster als PNG rendern ----------

    void SaveSnapshot(string path)
    {
        if (Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
