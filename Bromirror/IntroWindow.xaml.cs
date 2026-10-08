using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Bromirror;

/// <summary>HUD-Boot-Intro im Orange-Look. Klick/Esc ueberspringt.</summary>
public partial class IntroWindow : Window
{
    const double Center = 390;
    const double TotalSeconds = 3.4;
    const double LineInterval = 0.45;
    const string BrandName = "BROMIRROR";
    static readonly string[] BootLines =
    {
        "SYSTEMKERN WIRD INITIALISIERT",
        "AIRPLAY-PROTOKOLL ········ OK",
        "MDNS-BEACON ·········· ONLINE",
        "D3D11-RENDERER ······· BEREIT",
        "WATCHDOG ·············· AKTIV",
        "WILLKOMMEN ZURÜCK, SIR.",
    };
    static readonly Color Orange = Color.FromRgb(0xFF, 0x7A, 0x1A);

    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Stopwatch _elapsed = new();
    readonly string? _snapshotPath;
    bool _snapshotTaken;
    bool _ending;

    public event Action? Finished;

    public IntroWindow(string? snapshotPath = null)
    {
        InitializeComponent();
        _snapshotPath = snapshotPath;
        BuildTicks();
        BuildSweep();
        Loaded += OnLoaded;
        MouseDown += (_, _) => End();
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Space or Key.Enter) End(); };
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Fade(Backdrop, 0, 0.35, 1);
        Fade(TickCanvas, 0.2, 0.6, 1);
        Fade(SweepHost, 0.5, 0.4, 1);
        Fade(PercentText, 0.25, 0.3, 1);
        Spin(SweepHost, 2.4);

        Ring(Ring1, delay: 0.05, period: 60, clockwise: true);
        Ring(Ring2, delay: 0.15, period: 14, clockwise: false);
        Ring(Ring3, delay: 0.25, period: 0, clockwise: true);
        Ring(Ring4, delay: 0.30, period: 30, clockwise: true, targetOpacity: 0.7);
        Ring(Arc1, delay: 0.35, period: 3.2, clockwise: true);
        Ring(Arc2, delay: 0.40, period: 5, clockwise: false);

        var pop = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.6))
        {
            BeginTime = TimeSpan.FromSeconds(0.45),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        CoreScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CoreScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        Fade(Core, 0.45, 0.3, 1);
        CoreGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(40, 80, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        _elapsed.Start();
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
    }

    void Tick()
    {
        double t = _elapsed.Elapsed.TotalSeconds;

        double p = Math.Clamp((t - 0.2) / 2.4, 0, 1);
        PercentText.Text = $"{(int)(EaseOut(p) * 100):000}%";

        int letters = (int)Math.Clamp((t - 1.1) / 0.08, 0, BrandName.Length);
        TitleText.Text = string.Join(" ", BrandName[..letters].ToCharArray());
        SubtitleText.Opacity = Math.Clamp((t - 1.9) / 0.4, 0, 1);

        double bootT = t - 0.3;
        if (bootT >= 0)
        {
            int line = Math.Min((int)(bootT / LineInterval), BootLines.Length - 1);
            string text = BootLines[line];
            int chars = (int)Math.Clamp((bootT - line * LineInterval) / 0.012, 0, text.Length);
            bool cursor = (int)(t * 6) % 2 == 0;
            StatusText.Text = "> " + text[..chars] + (cursor ? "▌" : " ");
        }

        if (_snapshotPath != null && !_snapshotTaken && t >= 2.95)
        {
            _snapshotTaken = true;
            SaveSnapshot(_snapshotPath);
        }
        if (t >= TotalSeconds) End();
    }

    void End()
    {
        if (_ending) return;
        _ending = true;
        var d = TimeSpan.FromSeconds(0.45);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var zoom = new DoubleAnimation(1, 1.12, d) { EasingFunction = ease };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        var fade = new DoubleAnimation(1, 0, d) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            _clock.Stop();
            Close();
            Finished?.Invoke();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- Bausteine ----------

    void BuildTicks()
    {
        for (int i = 0; i < 72; i++)
        {
            double a = i * 5 * Math.PI / 180;
            bool major = i % 6 == 0;
            double r1 = 318, r2 = major ? 336 : 326;
            TickCanvas.Children.Add(new Line
            {
                X1 = Center + r1 * Math.Cos(a), Y1 = Center + r1 * Math.Sin(a),
                X2 = Center + r2 * Math.Cos(a), Y2 = Center + r2 * Math.Sin(a),
                Stroke = new SolidColorBrush(Orange),
                StrokeThickness = major ? 2 : 1,
                Opacity = major ? 0.9 : 0.45,
            });
        }
    }

    void BuildSweep()
    {
        const double r = 300, spread = 55;
        var start = PointAt(-spread, r);
        var end = PointAt(0, r);
        var figure = new PathFigure { StartPoint = new Point(Center, Center), IsClosed = true };
        figure.Segments.Add(new LineSegment(start, false));
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, false, SweepDirection.Clockwise, false));
        SweepHost.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { figure }),
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0xFF, 0x7A, 0x1A), Color.FromArgb(38, 0xFF, 0x7A, 0x1A),
                new Point(0, 0), new Point(1, 1)),
        });
        SweepHost.Children.Add(new Line
        {
            X1 = Center, Y1 = Center, X2 = end.X, Y2 = end.Y,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x67)), StrokeThickness = 1.2, Opacity = 0.6,
        });
    }

    static Point PointAt(double degrees, double r)
    {
        double a = degrees * Math.PI / 180;
        return new Point(Center + r * Math.Cos(a), Center + r * Math.Sin(a));
    }

    static void Ring(UIElement ring, double delay, double period, bool clockwise, double targetOpacity = 1)
    {
        var scale = new ScaleTransform(0.75, 0.75);
        var rotate = new RotateTransform();
        ring.RenderTransformOrigin = new Point(0.5, 0.5);
        ring.RenderTransform = new TransformGroup { Children = { scale, rotate } };

        var grow = new DoubleAnimation(0.75, 1, TimeSpan.FromSeconds(0.8))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Fade(ring, delay, 0.5, targetOpacity);

        if (period > 0)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, clockwise ? 360 : -360, TimeSpan.FromSeconds(period)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    static void Spin(UIElement el, double period)
    {
        var rotate = new RotateTransform(0, Center, Center);
        el.RenderTransform = rotate;
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(period)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    static void Fade(UIElement el, double delay, double duration, double to) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, to, TimeSpan.FromSeconds(duration))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
        });

    static double EaseOut(double x) => 1 - Math.Pow(1 - x, 3);

    void SaveSnapshot(string path)
    {
        var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
