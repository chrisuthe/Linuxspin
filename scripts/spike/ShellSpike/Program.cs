// Shell spike: measures what the windowing layer does when asked to follow the desktop, and what
// the living-backdrop effect loop costs. Every number in the "UI shell" section of
// docs/ARCHITECTURE.md comes from one of the modes below. Output lines are prefixed "[spike]" so
// they can be grepped out of Avalonia's own logging.
//
//   ShellSpike theme   [--seconds N]   theme variant + accent, live, with a rendered-pixel sample
//   ShellSpike font                    what family the default text resolves to
//   ShellSpike chrome  [--seconds N]   ExtendClientAreaToDecorationsHint and what it reports
//   ShellSpike clock                   how fast each candidate animation clock actually ticks
//   ShellSpike effects <case> [--seconds N]
//       case: baseline | gradient-mutate | gradient-swap | glow-boxshadow | glow-dropshadow | blur-once
//
// Environment:
//   SENDSPIN_X11=1            X11 backend (same variable as the player); default is Wayland when
//                             WAYLAND_DISPLAY is set, X11 otherwise. Other platforms: platform detect.
//   SPIKE_SOFTWARE=1          X11: RenderingMode = [Software]. Wayland has no such option (see doc).
//   SPIKE_NO_INTER=1          skip WithInterFont()
//   SPIKE_FONT_SHAPE=a|b      a: FontManagerOptions.FontFallbacks=[Inter] + resource "$Default"
//                             b: resource "$Default, fonts:Inter#Inter"
//   SPIKE_FORCE_CSD=1         Wayland: ForceDrawnDecorations; X11: EnableDrawnDecorations
//   SPIKE_TITLEBAR=<double>   ExtendClientAreaTitleBarHeightHint (default -1)
//   SPIKE_TRANSPARENCY=a,b    TransparencyLevelHint list: mica|acrylic|blur|transparent|none
//   SPIKE_DRIVER=raf|timer|threadtimer  effects: what paces the mutation loop (default raf)
//   SPIKE_OVERLAY=1           RendererDebugOverlays.Fps on the probe window
//   SPIKE_SCREENSHOT=<path>   a command run once the window has settled, e.g. "spectacle -b -n -a -o /tmp/x.png"

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ShellSpike;

internal static class Env
{
    public static bool Flag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

internal static class Log
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Line(string s) =>
        Console.WriteLine($"[spike] {DateTime.Now:HH:mm:ss.fff} {s}");
}

internal static class Program
{
    public static string Mode = "theme";
    public static string Case = "baseline";
    public static double Seconds = 0;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) Mode = args[0];
        var rest = args.Skip(1).ToList();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i] == "--seconds" && i + 1 < rest.Count) { Seconds = double.Parse(rest[++i], CultureInfo.InvariantCulture); continue; }
            Case = rest[i];
        }

        Log.Line($"mode={Mode} case={Case} seconds={Seconds} os={RuntimeInformation.OSDescription} avalonia={typeof(AppBuilder).Assembly.GetName().Version}");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string Backend = "platform-detect";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();

        if (OperatingSystem.IsLinux())
        {
            var x11 = Env.Flag("SENDSPIN_X11") || string.IsNullOrEmpty(Env.Get("WAYLAND_DISPLAY"));
            if (x11)
            {
                Backend = "X11";
                var opts = new X11PlatformOptions();
                if (Env.Flag("SPIKE_SOFTWARE")) opts.RenderingMode = new[] { X11RenderingMode.Software };
                if (Env.Flag("SPIKE_FORCE_CSD")) opts.EnableDrawnDecorations = true;
                builder = builder.UseX11().With(opts);
            }
            else
            {
                Backend = "Wayland";
                var opts = new WaylandPlatformOptions();
                if (Env.Flag("SPIKE_FORCE_CSD")) opts.ForceDrawnDecorations = true;
                builder = builder.UseWayland().With(opts);
            }

            builder = builder.UseSkia().UseHarfBuzz();
        }
        else
        {
            builder = builder.UsePlatformDetect();
        }

        if (!Env.Flag("SPIKE_NO_INTER")) builder = builder.WithInterFont();

        if (Env.Get("SPIKE_FONT_SHAPE") == "a")
        {
            // Candidate shape: leave DefaultFamilyName unset so "$Default" stays the platform's
            // answer, and list Inter as a glyph fallback only.
            builder = builder.With(new FontManagerOptions
            {
                FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("fonts:Inter#Inter") } },
            });
        }

        return builder.LogToTrace();
    }
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;

        var shape = Env.Get("SPIKE_FONT_SHAPE");
        if (shape == "a") Resources["ContentControlThemeFontFamily"] = new FontFamily("$Default");
        if (shape == "b") Resources["ContentControlThemeFontFamily"] = new FontFamily("$Default, fonts:Inter#Inter");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Window window = Program.Mode switch
            {
                "theme" => new ThemeWindow(),
                "font" => new FontWindow(),
                "chrome" => new ChromeWindow(),
                "clock" => new ClockWindow(),
                "effects" => new EffectsWindow(Program.Case),
                _ => throw new ArgumentException($"unknown mode {Program.Mode}"),
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Probe
{
    public static string Colour(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static string Values(PlatformColorValues v) =>
        $"ThemeVariant={v.ThemeVariant} AccentColor1={Colour(v.AccentColor1)} AccentColor2={Colour(v.AccentColor2)} AccentColor3={Colour(v.AccentColor3)} Contrast={v.ContrastPreference}";

    public static void Common(Window w)
    {
        Log.Line($"backend={Program.Backend} software-requested={Env.Flag("SPIKE_SOFTWARE")} render-scaling={w.RenderScaling}");
        if (Env.Flag("SPIKE_OVERLAY")) w.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.Fps;
    }

    // Which GL stack (if any) the process actually loaded — the honest answer to "did software
    // rendering happen", read from the loader rather than from what we asked for.
    public static void MappedGraphicsLibraries()
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var libs = File.ReadAllLines("/proc/self/maps")
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
                .Where(p => p.Contains("libEGL") || p.Contains("libGLX") || p.Contains("libGL.") || p.Contains("nvidia") || p.Contains("gallium") || p.Contains("_dri") || p.Contains("llvmpipe") || p.Contains("swrast") || p.Contains("libvulkan") || p.Contains("libwayland-egl") || p.Contains("libSkiaSharp"))
                .Select(System.IO.Path.GetFileName)
                .Distinct()
                .OrderBy(x => x);
            Log.Line("mapped graphics libraries: " + string.Join(", ", libs));
            var nodes = Directory.EnumerateFiles("/proc/self/fd")
                .Select(f => { try { return new FileInfo(f).LinkTarget ?? ""; } catch { return ""; } })
                .Where(t => t.StartsWith("/dev/dri/"))
                .Distinct()
                .Select(t =>
                {
                    var name = System.IO.Path.GetFileName(t);
                    string vendor = "?";
                    try { vendor = File.ReadAllText($"/sys/class/drm/{name}/device/vendor").Trim(); } catch { }
                    return $"{t} (vendor {vendor})";
                });
            Log.Line("open DRM nodes: " + string.Join(", ", nodes));
        }
        catch (Exception e) { Log.Line("maps unreadable: " + e.Message); }
    }

    public static async void Screenshot(Window w, int delayMs = 1500)
    {
        var cmd = Env.Get("SPIKE_SCREENSHOT");
        if (string.IsNullOrEmpty(cmd)) return;
        await Task.Delay(delayMs);
        try
        {
            var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var p = Process.Start(new ProcessStartInfo(parts[0], string.Join(' ', parts.Skip(1))) { UseShellExecute = false });
            p?.WaitForExit();
            Log.Line($"screenshot command exited {p?.ExitCode}");
        }
        catch (Exception e) { Log.Line("screenshot failed: " + e.Message); }
    }

    public static void ExitAfter(Window w, double seconds)
    {
        if (seconds <= 0) return;
        DispatcherTimer.RunOnce(() => w.Close(), TimeSpan.FromSeconds(seconds));
    }

    public static Color SamplePixel(Control c, int? x = null)
    {
        var size = new PixelSize(Math.Max(1, (int)c.Bounds.Width), Math.Max(1, (int)c.Bounds.Height));
        using var rtb = new RenderTargetBitmap(size);
        rtb.Render(c);
        var buf = new byte[4];
        unsafe
        {
            fixed (byte* p = buf)
            {
                rtb.CopyPixels(new PixelRect(x ?? size.Width / 2, size.Height / 2, 1, 1), (nint)p, 4, 4);
            }
        }
        // Bgra8888 on every Skia platform this runs on.
        return Color.FromArgb(buf[3], buf[2], buf[1], buf[0]);
    }
}

// ---------------------------------------------------------------------------------------------
// theme
// ---------------------------------------------------------------------------------------------
internal sealed class ThemeWindow : Window
{
    private readonly Border _accent;
    private readonly Button _accentButton;
    private readonly TextBlock _report;

    public ThemeWindow()
    {
        Title = "ShellSpike theme";
        Width = 880; Height = 600;
        _accent = new Border { Height = 140, Margin = new Thickness(0, 12, 0, 0) };
        _accent.Bind(Border.BackgroundProperty, _accent.GetResourceObservable("SystemAccentColor", v => v is Color c ? new SolidColorBrush(c) : null));
        _report = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), FontSize = 12 };
        _accentButton = new Button { Content = "Accent button", Classes = { "accent" }, Padding = new Thickness(24, 8) };
        var slider = new Slider { Value = 60, Minimum = 0, Maximum = 100, Width = 400 };
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Theme / accent probe — the band below is SystemAccentColor as rendered", FontSize = 18 },
                _accentButton, slider, new CheckBox { Content = "checked", IsChecked = true }, _accent, _report,
            },
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Probe.Common(this);
        var settings = Application.Current!.PlatformSettings!;
        Report("opened");
        settings.ColorValuesChanged += (_, v) => Report("ColorValuesChanged: " + Probe.Values(v));
        Application.Current!.ActualThemeVariantChanged += (_, _) => Report("Application.ActualThemeVariantChanged");
        ActualThemeVariantChanged += (_, _) => Report("Window.ActualThemeVariantChanged");
        // The portal read is asynchronous; give it a beat and report the settled state.
        DispatcherTimer.RunOnce(() => Report("settled"), TimeSpan.FromMilliseconds(800));
        Probe.Screenshot(this);
        Probe.ExitAfter(this, Program.Seconds);
    }

    private void Report(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var settings = Application.Current!.PlatformSettings!;
            var values = settings.GetColorValues();
            Application.Current!.TryGetResource("SystemAccentColor", ActualThemeVariant, out var res);
            var rendered = Probe.SamplePixel(_accent);
            var renderedButton = Probe.SamplePixel(_accentButton, 6);
            var line = $"{reason}: ActualThemeVariant={ActualThemeVariant} PlatformSettings=[{Probe.Values(values)}] SystemAccentColor resource={(res is Color rc ? Probe.Colour(rc) : res?.ToString() ?? "null")} rendered-accent-band-pixel={Probe.Colour(rendered)} rendered-accent-button-pixel={Probe.Colour(renderedButton)}";
            Log.Line(line);
            _report.Text = line;
            if (reason == "settled") Probe.MappedGraphicsLibraries();
        }, DispatcherPriority.Background);
    }
}

// ---------------------------------------------------------------------------------------------
// font
// ---------------------------------------------------------------------------------------------
internal sealed class FontWindow : Window
{
    private readonly TextBlock _plain = new() { Text = "The quick brown fox — default text, no FontFamily set" };
    private readonly Button _button = new() { Content = "Button text" };

    public FontWindow()
    {
        Title = "ShellSpike font";
        Width = 700; Height = 200;
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 8, Children = { _plain, _button } };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Probe.Common(this);
        var fm = FontManager.Current;
        Log.Line($"SPIKE_NO_INTER={Env.Flag("SPIKE_NO_INTER")} SPIKE_FONT_SHAPE={Env.Get("SPIKE_FONT_SHAPE") ?? "(none)"}");
        Log.Line($"FontManager.DefaultFontFamily (the platform's answer for $Default) = {fm.DefaultFontFamily}");
        Log.Line($"$Default -> {Resolve(new FontFamily("$Default"))}");
        Log.Line($"fonts:Inter#Inter -> {Resolve(new FontFamily("fonts:Inter#Inter"))}");
        Log.Line($"'Inter' by plain name (system fonts) -> {Resolve(new FontFamily("Inter"))}");
        Application.Current!.TryGetResource("ContentControlThemeFontFamily", ActualThemeVariant, out var res);
        Log.Line($"ContentControlThemeFontFamily resource = {res}");
        Log.Line($"TextBlock.FontFamily = {_plain.FontFamily} -> {Resolve(_plain.FontFamily, _plain.FontWeight)}");
        Log.Line($"Button.FontFamily = {_button.FontFamily} -> {Resolve(_button.FontFamily, _button.FontWeight)}");
        var fallback = fm.TryMatchCharacter('A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, _plain.FontFamily, CultureInfo.CurrentCulture, out var tf) ? $"{tf.FontFamily}" : "(none)";
        Log.Line($"TryMatchCharacter('A', TextBlock family) -> {fallback}");
        var cjk = fm.TryMatchCharacter(0x65E5, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, _plain.FontFamily, CultureInfo.CurrentCulture, out var tf2) ? $"{tf2.FontFamily}" : "(none)";
        Log.Line($"TryMatchCharacter(U+65E5 日, TextBlock family) -> {cjk}");
        Probe.MappedGraphicsLibraries();
        Probe.Screenshot(this);
        Probe.ExitAfter(this, Program.Seconds > 0 ? Program.Seconds : 1.5);
    }

    private static string Resolve(FontFamily family, FontWeight weight = FontWeight.Normal) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, weight), out var gt)
            ? $"glyph typeface '{gt.FamilyName}' weight={gt.Weight}"
            : "(unresolved)";
}

// ---------------------------------------------------------------------------------------------
// chrome
// ---------------------------------------------------------------------------------------------
internal sealed class ChromeWindow : Window
{
    private readonly TextBlock _report;

    public ChromeWindow()
    {
        Title = "ShellSpike chrome";
        Width = 880; Height = 600;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = double.Parse(Env.Get("SPIKE_TITLEBAR") ?? "-1", CultureInfo.InvariantCulture);
        var t = Env.Get("SPIKE_TRANSPARENCY");
        if (!string.IsNullOrEmpty(t))
        {
            TransparencyLevelHint = t.Split(',').Select(s => s.Trim().ToLowerInvariant() switch
            {
                "mica" => WindowTransparencyLevel.Mica,
                "acrylic" => WindowTransparencyLevel.AcrylicBlur,
                "blur" => WindowTransparencyLevel.Blur,
                "transparent" => WindowTransparencyLevel.Transparent,
                _ => WindowTransparencyLevel.None,
            }).ToList();
            Background = new SolidColorBrush(Colors.Black, 0.35);
        }

        _report = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), FontSize = 12, Margin = new Thickness(16) };
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new Border
                {
                    Height = 48, Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x40, 0x40)),
                    Child = new TextBlock { Text = "Top 48 px of the CONTENT — if this sits under the title bar, the client area was extended", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), Foreground = Brushes.White },
                },
                Grid1(_report),
            },
        };
    }

    private static Control Grid1(Control c) { Grid.SetRow(c, 1); return c; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Probe.Common(this);
        Report("opened");
        DispatcherTimer.RunOnce(() => Report("settled"), TimeSpan.FromMilliseconds(800));
        Probe.Screenshot(this);
        Probe.ExitAfter(this, Program.Seconds);
    }

    private void Report(string reason)
    {
        var line = $"{reason}: hint={ExtendClientAreaToDecorationsHint} titlebar-hint={ExtendClientAreaTitleBarHeightHint} IsExtendedIntoWindowDecorations={IsExtendedIntoWindowDecorations} WindowDecorationMargin={WindowDecorationMargin} OffScreenMargin={OffScreenMargin} WindowDecorations={WindowDecorations} TransparencyLevelHint=[{string.Join(",", TransparencyLevelHint)}] ActualTransparencyLevel={ActualTransparencyLevel} ClientSize={ClientSize} FrameSize={FrameSize} Position={Position}";
        Log.Line(line);
        _report.Text = line;
    }
}

// ---------------------------------------------------------------------------------------------
// effects
// ---------------------------------------------------------------------------------------------
internal sealed class EffectsWindow : Window
{
    private readonly string _case;
    private readonly Canvas _canvas = new();
    private readonly TextBlock _counter = new() { Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(12) };
    private readonly List<Ellipse> _ellipses = new();
    private readonly List<RadialGradientBrush> _brushes = new();
    private readonly List<ScaleTransform> _scales = new();
    private readonly List<TranslateTransform> _translates = new();
    private Border? _glow;
    private DropShadowEffect? _dropShadow;
    private BoxShadow _boxShadow;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<double> _periods = new();
    private readonly List<double> _callbackMs = new();
    private double _lastFrame = -1;
    private int _frames;
    private const double WarmupMs = 3000; // lets tiered JIT and first-frame allocation settle
    private TimeSpan _cpuAtStart;
    private Dictionary<string, long>? _threadsAtStart;
    private bool _measuring;
    private bool _done;

    public EffectsWindow(string @case)
    {
        _case = @case;
        Title = $"ShellSpike effects {@case}";
        Width = 880; Height = 600;
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16));
        var root = new Panel();
        root.Children.Add(_canvas);

        switch (@case)
        {
            case "baseline":
                break;
            case "gradient-mutate":
            case "gradient-swap":
                for (var i = 0; i < 3; i++)
                {
                    var brush = new RadialGradientBrush
                    {
                        GradientStops = { new GradientStop(Hue(i * 120), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
                    };
                    var scale = new ScaleTransform(1, 1);
                    var translate = new TranslateTransform(0, 0);
                    var ellipse = new Ellipse
                    {
                        Width = 620, Height = 620,
                        Fill = @case == "gradient-mutate" ? brush : brush.ToImmutable(),
                        RenderTransform = new TransformGroup { Children = { scale, translate } },
                        RenderTransformOrigin = RelativePoint.Center,
                    };
                    Canvas.SetLeft(ellipse, 60 + i * 120);
                    Canvas.SetTop(ellipse, -40 + i * 60);
                    _ellipses.Add(ellipse); _brushes.Add(brush); _scales.Add(scale); _translates.Add(translate);
                    _canvas.Children.Add(ellipse);
                }
                break;
            case "glow-boxshadow":
                _boxShadow = new BoxShadow { Blur = 20, Color = Color.FromArgb(0xC0, 0x3D, 0xAE, 0xE9) };
                _glow = Tile();
                _glow.BoxShadow = new BoxShadows(_boxShadow);
                root.Children.Add(_glow);
                break;
            case "glow-dropshadow":
                _dropShadow = new DropShadowEffect { BlurRadius = 20, Color = Color.FromArgb(0xC0, 0x3D, 0xAE, 0xE9), OffsetX = 0, OffsetY = 0 };
                _glow = Tile();
                _glow.Effect = _dropShadow;
                root.Children.Add(_glow);
                break;
            case "blur-once":
                var small = MakeArtwork(512).CreateScaledBitmap(new PixelSize(64, 64), BitmapInterpolationMode.MediumQuality);
                Log.Line($"blur-once: 512 px artwork -> CreateScaledBitmap 64x64 -> Image 880x600 with BlurEffect(32); scaled bitmap is {small.PixelSize}");
                var image = new Image { Source = small, Stretch = Stretch.Fill, Width = 880, Height = 600, Effect = new BlurEffect { Radius = 32 } };
                root.Children.Insert(0, image);
                break;
            default:
                throw new ArgumentException($"unknown case {@case}");
        }

        root.Children.Add(_counter);
        Content = root;
    }

    private static Border Tile() => new()
    {
        Width = 320, Height = 320, CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x60)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Color Hue(double deg)
    {
        var h = ((deg % 360) + 360) % 360 / 60.0;
        var x = (byte)(255 * (1 - Math.Abs(h % 2 - 1)));
        return (int)h switch
        {
            0 => Color.FromRgb(255, x, 0), 1 => Color.FromRgb(x, 255, 0), 2 => Color.FromRgb(0, 255, x),
            3 => Color.FromRgb(0, x, 255), 4 => Color.FromRgb(x, 0, 255), _ => Color.FromRgb(255, 0, x),
        };
    }

    // CreateScaledBitmap throws "Invalid source bitmap type" for a WriteableBitmap, so the artwork
    // goes through a PNG round-trip first — which is also how real artwork arrives.
    private static Bitmap MakeArtwork(int size)
    {
        var wb = MakeWriteable(size);
        var ms = new MemoryStream();
        wb.Save(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static WriteableBitmap MakeWriteable(int size)
    {
        var wb = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            unsafe
            {
                var p = (byte*)fb.Address;
                var rnd = new Random(1);
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var o = y * fb.RowBytes + x * 4;
                        p[o] = (byte)(x * 255 / size); p[o + 1] = (byte)(y * 255 / size); p[o + 2] = (byte)rnd.Next(256); p[o + 3] = 255;
                    }
            }
        }
        return wb;
    }

    private static readonly string Driver = Env.Get("SPIKE_DRIVER") ?? "raf";
    private static readonly bool TimerDriven = Driver != "raf";
    private DispatcherTimer? _timer;
    private System.Threading.Timer? _threadTimer;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Probe.Common(this);
        Log.Line($"driver={Driver switch { "timer" => "DispatcherTimer 16 ms (Render priority)", "threadtimer" => "System.Threading.Timer 16 ms -> Dispatcher.Post(Render)", _ => "RequestAnimationFrame re-armed in the callback" }}");
        switch (Driver)
        {
            case "timer":
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Frame(TimeSpan.Zero));
                _timer.Start();
                break;
            case "threadtimer":
                _threadTimer = new System.Threading.Timer(_ => Dispatcher.UIThread.Post(() => Frame(TimeSpan.Zero), DispatcherPriority.Render), null, 16, 16);
                break;
            default:
                RequestAnimationFrame(Frame);
                break;
        }
        Probe.Screenshot(this);
    }

    private void Frame(TimeSpan _)
    {
        if (_done) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        var t = now / 1000.0;
        var started = Stopwatch.GetTimestamp();

        switch (_case)
        {
            case "gradient-mutate":
                for (var i = 0; i < 3; i++)
                {
                    _scales[i].ScaleX = _scales[i].ScaleY = 1 + 0.25 * Math.Sin(t * 0.7 + i);
                    _translates[i].X = 120 * Math.Sin(t * 0.5 + i * 2); _translates[i].Y = 80 * Math.Cos(t * 0.4 + i);
                    _brushes[i].GradientStops[0].Color = Hue(t * 40 + i * 120);
                }
                break;
            case "gradient-swap":
                for (var i = 0; i < 3; i++)
                {
                    _scales[i].ScaleX = _scales[i].ScaleY = 1 + 0.25 * Math.Sin(t * 0.7 + i);
                    _translates[i].X = 120 * Math.Sin(t * 0.5 + i * 2); _translates[i].Y = 80 * Math.Cos(t * 0.4 + i);
                    _ellipses[i].Fill = new ImmutableRadialGradientBrush(
                        new[] { new ImmutableGradientStop(0, Hue(t * 40 + i * 120)), new ImmutableGradientStop(1, Color.FromArgb(0, 0, 0, 0)) },
                        1.0, null, null, GradientSpreadMethod.Pad, null, null, 0.5);
                }
                break;
            case "glow-boxshadow":
                _boxShadow.Blur = 35 + 25 * Math.Sin(t * 2);
                _glow!.BoxShadow = new BoxShadows(_boxShadow);
                break;
            case "glow-dropshadow":
                _dropShadow!.BlurRadius = 35 + 25 * Math.Sin(t * 2);
                break;
        }

        _frames++;
        _counter.Text = $"frame {_frames}";
        var callback = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (!_measuring && now >= WarmupMs)
        {
            _measuring = true;
            _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
            _threadsAtStart = ThreadCpu();
            _lastFrame = now;
        }
        else if (_measuring)
        {
            _periods.Add(now - _lastFrame);
            _callbackMs.Add(callback);
            _lastFrame = now;
            if (now - WarmupMs >= (Program.Seconds > 0 ? Program.Seconds : 5) * 1000)
            {
                Finish();
                return;
            }
        }

        if (!TimerDriven) RequestAnimationFrame(Frame);
    }

    private void Finish()
    {
        _done = true;
        _timer?.Stop();
        _threadTimer?.Dispose();
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart).TotalMilliseconds;
        var frames = _periods.Count;
        var wall = _periods.Sum();
        var sorted = _callbackMs.OrderBy(x => x).ToList();
        var threads = ThreadCpu();
        var byThread = string.Join(", ", threads
            .Select(kv => (kv.Key, ms: (kv.Value - (_threadsAtStart!.GetValueOrDefault(kv.Key))) * 1000.0 / ClockTicks / frames))
            .Where(x => x.ms > 0.05).OrderByDescending(x => x.ms).Take(4)
            .Select(x => $"{x.Key}={x.ms:F2}"));
        Log.Line($"RESULT case={_case} backend={Program.Backend} software={Env.Flag("SPIKE_SOFTWARE")} driver={Driver} frames={frames} wall-ms/frame={wall / frames:F2} (fps={1000 * frames / wall:F1}) cpu-ms/frame={cpu / frames:F2} callback-ms mean={_callbackMs.Average():F3} p95={sorted[(int)(sorted.Count * 0.95)]:F3} per-thread-cpu-ms/frame=[{byThread}]");
        Probe.MappedGraphicsLibraries();
        Close();
    }

    private static readonly long ClockTicks = 100; // Linux CLK_TCK

    private static Dictionary<string, long> ThreadCpu()
    {
        var result = new Dictionary<string, long>();
        if (!OperatingSystem.IsLinux()) return result;
        foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                var stat = File.ReadAllText(System.IO.Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var name = stat.Substring(stat.IndexOf('(') + 1, close - stat.IndexOf('(') - 1);
                var fields = stat[(close + 2)..].Split(' ');
                var ticks = long.Parse(fields[11]) + long.Parse(fields[12]);
                var key = $"{name}#{System.IO.Path.GetFileName(dir)}";
                result[key] = ticks;
            }
            catch { }
        }
        return result;
    }
}


// ---------------------------------------------------------------------------------------------
// clock — which candidate animation clocks tick at the rate they were asked for
// ---------------------------------------------------------------------------------------------
internal sealed class ClockWindow : Window
{
    private readonly TextBlock _counter = new() { Margin = new Thickness(12) };
    private int _ticks;

    public ClockWindow()
    {
        Title = "ShellSpike clock";
        Width = 880; Height = 600;
        Content = _counter;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Probe.Common(this);
        await Task.Delay(1000);

        await Measure("RequestAnimationFrame re-armed", stop =>
        {
            void Cb(TimeSpan _) { Tick(); if (!stop.IsCancellationRequested) RequestAnimationFrame(Cb); }
            RequestAnimationFrame(Cb);
        });
        foreach (var prio in new[] { DispatcherPriority.Render, DispatcherPriority.Normal, DispatcherPriority.Background })
        {
            await Measure($"DispatcherTimer 16 ms @{prio}", stop =>
            {
                var t = new DispatcherTimer(TimeSpan.FromMilliseconds(16), prio, (_, _) => Tick());
                t.Start();
                stop.Register(t.Stop);
            });
        }
        foreach (var ms in new[] { 100, 500 })
        {
            await Measure($"DispatcherTimer {ms} ms @Normal", stop =>
            {
                var t = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal, (_, _) => Tick());
                t.Start();
                stop.Register(t.Stop);
            });
        }
        await Measure("System.Threading.Timer 16 ms -> Post(Render)", stop =>
        {
            var t = new System.Threading.Timer(_ => Dispatcher.UIThread.Post(Tick, DispatcherPriority.Render), null, 16, 16);
            stop.Register(() => t.Dispose());
        });
        await Measure("Task.Delay(16) loop on the UI thread", async stop =>
        {
            while (!stop.IsCancellationRequested) { await Task.Delay(16); Tick(); }
        });
        await Measure("Avalonia Animation (1 s loop) property ticks", stop =>
        {
            var anim = new Avalonia.Animation.Animation
            {
                Duration = TimeSpan.FromSeconds(1), IterationCount = Avalonia.Animation.IterationCount.Infinite,
                Children =
                {
                    new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0.5) } },
                    new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(OpacityProperty, 1.0) } },
                },
            };
            var sub = _counter.GetObservable(OpacityProperty).Subscribe(new Observer(Tick));
            _ = anim.RunAsync(_counter, stop);
            stop.Register(sub.Dispose);
        });
        Probe.MappedGraphicsLibraries();
        Close();
    }

    private sealed class Observer(Action onNext) : IObserver<double>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(double value) => onNext();
    }

    private void Tick() { _ticks++; _counter.Text = $"ticks {_ticks}"; }

    private async Task Measure(string name, Action<CancellationToken> start)
    {
        using var cts = new CancellationTokenSource();
        _ticks = 0;
        var sw = Stopwatch.StartNew();
        start(cts.Token);
        await Task.Delay(3000);
        cts.Cancel();
        var elapsed = sw.Elapsed.TotalMilliseconds;
        Log.Line($"CLOCK {name}: {_ticks} ticks in {elapsed:F0} ms = {_ticks * 1000.0 / elapsed:F1} Hz ({elapsed / Math.Max(1, _ticks):F2} ms/tick)");
        await Task.Delay(300);
    }
}
