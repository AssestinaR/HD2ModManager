using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading;

namespace UiSnapshot;

// Entry point. Deliberately a console exe (not WinExe) so the CLI prints the
// produced path; WPF only needs an STA thread, not a Run() loop.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "list")
        {
            PrintHelp();
            return 0;
        }

        SnapshotOptions options;
        try
        {
            options = SnapshotOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // Load the real application resources (Themes/Controls.xaml,
        // Themes/DataTables.xaml, card/list styles) without starting the shell.
        var app = new HD2ModManager.App
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.InitializeComponent();

        try
        {
            var scenario = ScenarioCatalog.Resolve(options.Scenario);
            var content = scenario.Build();
            using var host = new RenderHost(content, scenario.Width, scenario.Height, scenario.Background);
            host.Show();
            scenario.Prepare?.Invoke(host.Content);
            host.Pump();
            scenario.Settle?.Invoke(host);

            var path = host.Save(options.OutputDirectory, options.Name ?? scenario.Name, options.Scale);
            Console.WriteLine(path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("UiSnapshot - offscreen renderer for HD2ModManager views");
        Console.WriteLine();
        Console.WriteLine("Usage: UiSnapshot <scenario> [--out <dir>] [--scale <n>] [--name <file>]");
        Console.WriteLine();
        Console.WriteLine("Scenarios:");
        foreach (var name in ScenarioCatalog.Names) Console.WriteLine("  " + name);
        Console.WriteLine();
        Console.WriteLine("Default output: artifacts/ui-audit/render");
        Console.WriteLine("Default scale: 2 (192 DPI)");
    }
}

internal sealed class SnapshotOptions
{
    public string Scenario { get; private init; } = "modlist";
    public string? Name { get; private init; }
    public string OutputDirectory { get; private init; } = Path.Combine(RepositoryRoot.Find(), "artifacts", "ui-audit", "render");
    public double Scale { get; private init; } = 2d;

    public static SnapshotOptions Parse(string[] args)
    {
        var scenario = args[0];
        string? name = null;
        string? outDir = null;
        var scale = 2d;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outDir = RequireValue(args, ref i);
                    break;
                case "--name":
                    name = RequireValue(args, ref i);
                    break;
                case "--scale":
                    if (!double.TryParse(RequireValue(args, ref i), NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale <= 0 || scale > 4)
                        throw new ArgumentException("--scale must be between 0 and 4.");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new SnapshotOptions
        {
            Scenario = scenario,
            Name = name,
            OutputDirectory = outDir ?? Path.Combine(RepositoryRoot.Find(), "artifacts", "ui-audit", "render"),
            Scale = scale
        };
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[index]}");
        return args[++index];
    }
}

internal static class RepositoryRoot
{
    // Walk up from the binary until the solution file is found, so the tool can
    // be launched from any working directory.
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpfApp1.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}

internal sealed class RenderHost : IDisposable
{
    private readonly Window _window;

    public FrameworkElement Content { get; }

    public RenderHost(FrameworkElement content, double width, double height, Brush background)
    {
        Content = content;
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Width = width,
            Height = height,
            Background = background,
            Content = content,
            UseLayoutRounding = true
        };
    }

    public void Show() => _window.Show();

    // Let data templates apply, images decode, and layout settle. Each round
    // drains the dispatcher down to ApplicationIdle.
    public void Pump(int rounds = 6)
    {
        for (var i = 0; i < rounds; i++)
        {
            DoEvents(DispatcherPriority.ApplicationIdle);
            Content.UpdateLayout();
        }
    }

    public string Save(string directory, string name, double scale)
    {
        Content.UpdateLayout();
        DoEvents(DispatcherPriority.Render);

        var width = Content.ActualWidth > 0 ? Content.ActualWidth : _window.Width;
        var height = Content.ActualHeight > 0 ? Content.ActualHeight : _window.Height;
        var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96d * scale, 96d * scale, PixelFormats.Pbgra32);
        bitmap.Render(Content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public static void DoEvents(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // 让动画按真实时间推进：只靠 DoEvents 会在几毫秒内跑完，截不到中间帧。
    public void PumpFor(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            DoEvents(DispatcherPriority.Render);
            Thread.Sleep(4);
        }
        DoEvents(DispatcherPriority.ApplicationIdle);
    }

    public void Dispose()
    {
        _window.Content = null;
        _window.Close();
        DoEvents(DispatcherPriority.ApplicationIdle);
    }
}

internal static class VisualTree
{
    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    public static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
        => Descendants(root).OfType<T>();

    public static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => All<T>(root).FirstOrDefault(element => string.Equals(element.Name, name, StringComparison.Ordinal));
}
