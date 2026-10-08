using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Bromirror;

/// <summary>Tab "PC → TV": Bedienung des <see cref="ScreenStreamer"/>.</summary>
public partial class MainWindow
{
    readonly ScreenStreamer _tv = new();
    readonly SolidColorBrush _tvBrush = new(Color.FromRgb(0x5A, 0x5E, 0x6B));
    readonly DispatcherTimer _tvTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    string? _tvShownState;

    /// <summary>Muss laufen, solange <c>_loading</c> noch true ist, damit das Vorbelegen nichts speichert.</summary>
    void InitTv()
    {
        TvPulse1.Stroke = _tvBrush;
        TvPulse2.Stroke = _tvBrush;
        TvRing.Stroke = _tvBrush;
        TvStatusTitle.Foreground = _tvBrush;
        AnimateRing(TvPulse1, TimeSpan.Zero);
        AnimateRing(TvPulse2, TimeSpan.FromSeconds(1.3));

        _tv.MonitorIndex = _settings.TvMonitor;
        _tv.TargetHeight = _settings.TvHeight;
        _tv.Fps = _settings.TvFps;
        _tv.ShowCursor = _settings.TvCursor;
        _tv.Log += line => Dispatcher.BeginInvoke(() => AddLog("PC → TV: " + line));

        BuildMonitorPills();
        (_settings.TvHeight <= 720 ? TvQuality720 : TvQuality1080).IsChecked = true;
        (_settings.TvFps switch { 15 => TvFps15, 30 => TvFps30, _ => TvFps24 }).IsChecked = true;
        TvCursorToggle.IsChecked = _settings.TvCursor;

        _tvTimer.Tick += (_, _) => RefreshTv();
        _tvTimer.Start();
        RefreshTv();
        Closing += (_, _) =>
        {
            _tvTimer.Stop();
            _tv.Dispose();
        };
    }

    void BuildMonitorPills()
    {
        TvMonitorPanel.Children.Clear();
        var monitors = Monitors.All();
        int selected = _settings.TvMonitor >= 0 && _settings.TvMonitor < monitors.Count ? _settings.TvMonitor : 0;
        foreach (var m in monitors)
        {
            var pill = new RadioButton
            {
                Style = (Style)FindResource("OptionPill"),
                GroupName = "TvMonitor",
                Content = m.Label,
                Tag = m.Index,
                IsChecked = m.Index == selected,
            };
            pill.Checked += TvMonitor_Checked;
            TvMonitorPanel.Children.Add(pill);
        }
    }

    void StartTv()
    {
        try
        {
            _tv.Start();
        }
        catch (InvalidOperationException ex)
        {
            AddLog("PC → TV: FEHLER: " + ex.Message);
            TvStatusSub.Text = ex.Message;
        }
        RefreshTv();
    }

    void RefreshTv()
    {
        var (frames, bytes) = _tv.TakeStats();
        bool running = _tv.IsRunning;
        int viewers = _tv.Viewers;
        string state = !running ? "off" : viewers > 0 ? "live" : "ready";
        if (state != _tvShownState)
        {
            ApplyTvState(state);
            _tvShownState = state;
        }

        TvViewersText.Text = viewers.ToString();
        TvFpsText.Text = running && viewers > 0 ? frames.ToString() : "–";
        TvRateText.Text = running && viewers > 0 ? $"{bytes * 8 / 1_000_000.0:0.0} Mbit" : "–";
        TvPowerButton.Content = running ? "SENDER STOPPEN" : "SENDER STARTEN";
        TvTestButton.IsEnabled = running;
        TvUrlPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TvUrlText.Text = _tv.Url ?? "";
        TvStepUrl.Text = _tv.Url ?? "Erscheint nach dem Start";
        if (TvPage.IsVisible) UpdatePreview(running);
    }

    void ApplyTvState(string state)
    {
        var (color, inner, title, sub, pulse) = state switch
        {
            "live" => (C("#3DDC97"), C("#9BF2CB"), "LIVE", "Dein PC ist gerade auf dem Fernseher.", true),
            "ready" => (C("#FF7A1A"), C("#FFB067"), "BEREIT", "Sender läuft – gib die Adresse im Browser des Fernsehers ein.", true),
            _ => (C("#5A5E6B"), C("#8D91A0"), "AUS", "Starte den Sender, dann öffnest du deinen PC im Browser des Fernsehers.", false),
        };
        var d = TimeSpan.FromMilliseconds(450);
        Animate(_tvBrush, SolidColorBrush.ColorProperty, color, d);
        Animate(TvCoreOuter, GradientStop.ColorProperty, color, d);
        Animate(TvCoreInner, GradientStop.ColorProperty, inner, d);
        Animate(TvCoreGlow, System.Windows.Media.Effects.DropShadowEffect.ColorProperty, color, d);
        TvStatusTitle.Text = title;
        TvStatusSub.Text = sub;
        TvPulseHost.Visibility = pulse ? Visibility.Visible : Visibility.Hidden;
    }

    void UpdatePreview(bool running)
    {
        var frame = running ? _tv.LatestFrame : null;
        if (frame == null)
        {
            TvPreview.Source = null;
            TvPreviewHint.Visibility = Visibility.Visible;
            return;
        }
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 420;
        image.StreamSource = new MemoryStream(frame);
        image.EndInit();
        image.Freeze();
        TvPreview.Source = image;
        TvPreviewHint.Visibility = Visibility.Collapsed;
    }

    // ---------- Handler ----------

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Feuert schon waehrend InitializeComponent, bevor alle Elemente existieren.
        if (TvTab == null || PhonePage == null || TvPage == null) return;
        bool tv = TvTab.IsChecked == true;
        PhonePage.Visibility = tv ? Visibility.Collapsed : Visibility.Visible;
        TvPage.Visibility = tv ? Visibility.Visible : Visibility.Collapsed;
        FooterInfo.Text = tv
            ? "PC → TV  ·  Livebild über HTTP  ·  v1.1"
            : $"UxPlay 1.74  ·  Port {MirrorEngine.BasePort}  ·  v1.1";
        if (tv) RefreshTv();
    }

    void TvPower_Click(object sender, RoutedEventArgs e)
    {
        if (_tv.IsRunning)
        {
            _tv.Stop();
            RefreshTv();
        }
        else
        {
            StartTv();
        }
    }

    void TvCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_tv.Url == null) return;
        Clipboard.SetText(_tv.Url);
        AddLog($"PC → TV: Adresse kopiert ({_tv.Url})");
    }

    void TvTest_Click(object sender, RoutedEventArgs e)
    {
        if (_tv.Url != null) Process.Start(new ProcessStartInfo(_tv.Url) { UseShellExecute = true });
    }

    void TvMonitor_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TvMonitor = Convert.ToInt32(((FrameworkElement)sender).Tag);
        _tv.MonitorIndex = _settings.TvMonitor;
        _settings.Save();
    }

    void TvQuality_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TvHeight = Convert.ToInt32(((FrameworkElement)sender).Tag);
        _tv.TargetHeight = _settings.TvHeight;
        _settings.Save();
    }

    void TvFps_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TvFps = Convert.ToInt32(((FrameworkElement)sender).Tag);
        _tv.Fps = _settings.TvFps;
        _settings.Save();
    }

    void TvCursor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TvCursor = TvCursorToggle.IsChecked == true;
        _tv.ShowCursor = _settings.TvCursor;
        _settings.Save();
    }
}
