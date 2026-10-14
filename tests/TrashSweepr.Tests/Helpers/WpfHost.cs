using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrashSweepr.Tests.Helpers;

/// <summary>
/// Uma única thread STA com um Application do WPF para todos os testes de interface
/// (o WPF permite um Application por processo e exige acesso pela thread que o criou).
/// </summary>
internal static class WpfHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            // O App real do trashsweepr: carrega App.xaml, com as mesmas cores e estilos da janela de verdade.
            var app = new TrashSweepr.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WPF test host" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    // A thread do WPF é compartilhada: cada chamada roda na língua do teste que a fez (ver TestCulture.Use).
    public static Task RunAsync(Action work) => RunAsync(() => { work(); return true; });

    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        return Host.Value.InvokeAsync(() =>
        {
            var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
            try
            {
                return work();
            }
            finally
            {
                (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
            }
        }).Task;
    }

    public static Task RunAsync(Func<Task> work)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        return Host.Value.InvokeAsync(() =>
        {
            // O código assíncrono herda a língua daqui pelo contexto de execução.
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
            return work();
        }).Task.Unwrap();
    }

    /// <summary>Deixa o WPF processar bindings e layout pendentes.</summary>
    public static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    public static void Layout(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        Flush();
        element.UpdateLayout();
    }

    public static void SavePng(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}

/// <summary>Coleta os erros de binding que o WPF normalmente só escreve na janela de saída.</summary>
internal sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _messages.Add(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);

    public static BindingErrorListener Attach()
    {
        var listener = new BindingErrorListener();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        return listener;
    }

    public void Detach() => PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
}
