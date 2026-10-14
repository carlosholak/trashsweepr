using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrashSweepr.Limpeza.Converters;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Services;
using TrashSweepr.Limpeza.ViewModels;
using TrashSweepr.Limpeza.Views;
using TrashSweepr.Tests.Helpers;

namespace TrashSweepr.Tests;

/// <summary>
/// Renderiza a interface de verdade (sem abrir janela) e confere que nenhum binding falha.
/// Com TRASHSWEEPR_UI_SNAPSHOT_DIR definido, também grava as imagens PNG.
/// </summary>
public sealed class UiRenderingTests
{
    private const double Width = 1320;
    private const double Height = 780;

    private static string? SnapshotDir => Environment.GetEnvironmentVariable("TRASHSWEEPR_UI_SNAPSHOT_DIR");

    /// <summary>Catálogo real com um computador falso em estados variados (aplicado, parcial, pendente, incompatível).</summary>
    private static async Task<LimpezaViewModel> BuildScenarioAsync(bool elevated = true)
    {
        var sys = new FakeSystem();
        sys.Windows.IsElevated = elevated;
        var catalog = TweakCatalogLoader.LoadFromDirectory(RepoPaths.TweaksDirectory);
        Assert.Empty(catalog.Issues);

        foreach (var package in catalog.Catalog.Tweaks.SelectMany(t => t.Apply).OfType<AppxAction>().Select(a => a.PackageName))
        {
            sys.Appx.Install(package);
        }

        foreach (var removed in new[] { "Clipchamp.Clipchamp", "Microsoft.SkypeApp", "Microsoft.BingNews" })
        {
            sys.Appx.Packages[removed] = sys.Appx.Packages[removed] with { InstalledForCurrentUser = false };
        }

        sys.Registry.Seed(RegistryHive.Hkcu, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", RegistryValueType.Dword, 0);
        sys.Registry.Seed(RegistryHive.Hkcu, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", RegistryValueType.Dword, 0);
        sys.Registry.Seed(RegistryHive.Hkcu, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", RegistryValueType.Dword, 1);
        sys.Services.Services["DiagTrack"] = new ServiceState(true, ServiceStartType.Automatic, true);
        sys.Tasks.Tasks[@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"] = new TaskState(true, true);
        sys.Tasks.Tasks[@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] = new TaskState(true, true);

        var vm = new LimpezaViewModel(
            catalog.Catalog, 0, sys.Engine, sys.Detector, sys.Windows, sys.RestorePoints, sys.Backups,
            new FakeDialogs(), new FakeRelauncher(), new FakeGate(), "logs");
        await vm.InitializeAsync();

        vm.ProfileFilter.Options.Single(o => o.Label == "Moderado").IsSelected = true;
        vm.SelectedTweak = vm.AllTweaks.First(t => t.Id == "services.diagtrack");
        return vm;
    }

    private static double Brightness(RenderTargetBitmap bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0] + pixel[1] + pixel[2]) / 3.0;
    }

    private static RenderTargetBitmap Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void SaveIfRequested(FrameworkElement element, string name)
    {
        if (SnapshotDir is { Length: > 0 } dir)
        {
            WpfHost.SavePng(element, Path.Combine(dir, name + ".png"));
        }
    }

    /// <summary>Mostra o conteúdo de uma janela (que não será exibida) com o fundo, a fonte e a cor que ela daria.</summary>
    private static Border HostLikeWindow(Window window, double width)
    {
        var content = (FrameworkElement)window.Content;
        var dataContext = window.DataContext;
        window.Content = null;
        content.DataContext = dataContext;

        var host = new Border { Background = (Brush)window.FindResource("Bg"), Child = content, Width = width };
        host.SetResourceReference(TextElement.ForegroundProperty, "Text");
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetFontSize(host, window.FontSize);

        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
        WpfHost.Flush();
        return host;
    }

    // ---------- Seção Limpeza do Windows ----------

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Cleanup_section_renders_without_binding_errors(bool resultsTab, bool elevated)
    {
        await WpfHost.RunAsync(async () =>
        {
            var listener = BindingErrorListener.Attach();
            try
            {
                var vm = await BuildScenarioAsync(elevated);
                if (resultsTab)
                {
                    await vm.RunCommand.ExecuteAsync(null); // simulação
                }

                var view = new LimpezaView { DataContext = vm };
                WpfHost.Layout(view, Width, Height);

                var list = FindFirst<ListBox>(view)!;
                Assert.True(list.ItemContainerGenerator.Items.Count > 0);
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));

                // O visual do trashsweepr (fundo claro) realmente chegou aos pixels.
                Assert.True(Brightness(Render(view), 4, 4) > 200);

                SaveIfRequested(view, $"limpeza-{(resultsTab ? "resultado" : "detalhes")}{(elevated ? string.Empty : "-sem-admin")}");
                Assert.True(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public async Task Cleanup_section_with_an_empty_catalog_renders_without_binding_errors()
    {
        await WpfHost.RunAsync(async () =>
        {
            var listener = BindingErrorListener.Attach();
            try
            {
                var sys = new FakeSystem();
                var vm = new LimpezaViewModel(
                    TweakCatalog.Empty, 0, sys.Engine, sys.Detector, sys.Windows, sys.RestorePoints, sys.Backups,
                    new FakeDialogs(), new FakeRelauncher(), new FakeGate(), "logs");
                await vm.InitializeAsync();

                var view = new LimpezaView { DataContext = vm };
                WpfHost.Layout(view, Width, Height);
                SaveIfRequested(view, "limpeza-vazia");

                Assert.True(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public async Task The_elevation_button_appears_only_on_the_administrator_banner()
    {
        await WpfHost.RunAsync(async () =>
        {
            var vm = await BuildScenarioAsync(elevated: false);
            var view = new LimpezaView { DataContext = vm };
            WpfHost.Layout(view, Width, Height);

            var button = FindAll<Button>(view).Single(b => Equals(b.Content, "Reabrir como administrador"));
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.NotNull(button.Command);

            var elevated = await BuildScenarioAsync(elevated: true);
            var elevatedView = new LimpezaView { DataContext = elevated };
            WpfHost.Layout(elevatedView, Width, Height);
            Assert.DoesNotContain(FindAll<Button>(elevatedView), b => Equals(b.Content, "Reabrir como administrador"));
        });
    }

    [Fact]
    public async Task The_whole_view_never_uses_more_than_the_maps_own_palette()
    {
        // O visual do Mapa tem uma cor de destaque; a Limpeza não pode trazer cores próprias nos controles.
        await WpfHost.RunAsync(() =>
        {
            var xaml = File.ReadAllText(Path.Combine(RepoPaths.Root, "src", "TrashSweepr", "Limpeza", "Views", "LimpezaView.xaml"));

            Assert.DoesNotMatch(@"(?i)(Color|Background|Foreground|BorderBrush|Fill)=""#[0-9a-f]{3,8}""", xaml);
            Assert.DoesNotContain("CornerRadius=\"1", xaml, StringComparison.Ordinal);
        });
    }

    // ---------- Janela do app: seções Disco e Limpeza ----------

    private static MainWindow CreateWindow(Func<(LimpezaViewModel, IDisposable)> factory) =>
        new() { LimpezaFactory = factory };

    private sealed class NoDispose : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Cleanup_is_only_created_when_the_user_opens_the_section_and_only_once()
    {
        await WpfHost.RunAsync(async () =>
        {
            var vm = await BuildScenarioAsync();
            var created = 0;
            var window = CreateWindow(() =>
            {
                created++;
                return (vm, new NoDispose());
            });

            Assert.Equal(0, created); // abrir o app não consulta o sistema
            Assert.Equal(Visibility.Visible, window.DiskPane.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LimpezaPane.Visibility);

            window.CleanupSection.IsChecked = true;
            Assert.Equal(1, created);
            Assert.Equal(Visibility.Collapsed, window.DiskPane.Visibility);
            Assert.Equal(Visibility.Visible, window.LimpezaPane.Visibility);
            Assert.Same(vm, window.LimpezaPane.DataContext);

            window.DiskSection.IsChecked = true;
            Assert.Equal(Visibility.Visible, window.DiskPane.Visibility);
            Assert.Equal(Visibility.Collapsed, window.LimpezaPane.Visibility);

            window.CleanupSection.IsChecked = true;
            Assert.Equal(1, created); // volta à seção sem recriar nem reconsultar
        });
    }

    [Fact]
    public async Task Map_shortcuts_do_not_swallow_keys_while_the_cleanup_section_is_open()
    {
        await WpfHost.RunAsync(async () =>
        {
            var vm = await BuildScenarioAsync();
            var window = CreateWindow(() => (vm, new NoDispose()));
            using var source = new HwndSource(new HwndSourceParameters("teste"));

            KeyEventArgs Press(Key key) => new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

            var inDisk = Press(Key.Back);
            window.RaiseEvent(inDisk);
            Assert.True(inDisk.Handled); // no mapa, Backspace sobe uma pasta

            window.CleanupSection.IsChecked = true;
            var inCleanup = Press(Key.Back);
            window.RaiseEvent(inCleanup);
            Assert.False(inCleanup.Handled); // na Limpeza o Backspace é do campo de busca

            var f5 = Press(Key.F5);
            window.RaiseEvent(f5);
            Assert.False(f5.Handled);
        });
    }

    [Fact]
    public async Task Both_sections_of_the_window_render_without_binding_errors()
    {
        await WpfHost.RunAsync(async () =>
        {
            var listener = BindingErrorListener.Attach();
            try
            {
                var vm = await BuildScenarioAsync();
                var window = CreateWindow(() => (vm, new NoDispose()));
                var root = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border { Background = (Brush)window.FindResource("Bg"), Child = root };
                host.SetResourceReference(TextElement.ForegroundProperty, "Text");
                TextElement.SetFontFamily(host, window.FontFamily);
                TextElement.SetFontSize(host, window.FontSize);

                WpfHost.Layout(host, Width, 820);
                SaveIfRequested(host, "janela-disco");

                window.CleanupSection.IsChecked = true;
                WpfHost.Layout(host, Width, 820);
                SaveIfRequested(host, "janela-limpeza");

                Assert.True(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    // ---------- Diálogos ----------

    [Fact]
    public async Task Risk_confirmation_dialog_renders_and_requires_the_checkbox()
    {
        await WpfHost.RunAsync(() =>
        {
            var listener = BindingErrorListener.Attach();
            try
            {
                var dialog = new ConfirmRisksDialog(
                [
                    new RiskConfirmationItem("Desativar serviço de telemetria (DiagTrack)", RiskLevel.Moderate, "Em PCs gerenciados por uma empresa o serviço pode ser necessário."),
                    new RiskConfirmationItem("Ajuste avançado", RiskLevel.Advanced, "Pode impedir a inicialização de programas."),
                ]);
                var model = (ConfirmRisksViewModel)dialog.DataContext;
                var content = HostLikeWindow(dialog, 580);

                Assert.False(model.Accepted);
                Assert.Equal(["Avançado", "Moderado"], model.Items.Select(i => i.RiskText));
                Assert.Equal([Tone.Danger, Tone.Warning], model.Items.Select(i => i.Tone));

                var continueButton = FindAll<Button>(content).Single(b => Equals(b.Content, "Continuar"));
                Assert.False(continueButton.IsEnabled);
                model.Accepted = true;
                WpfHost.Flush();
                Assert.True(continueButton.IsEnabled);
                model.Accepted = false;
                WpfHost.Flush();

                SaveIfRequested(content, "dialogo-riscos");
                Assert.True(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public async Task Message_dialog_shows_title_text_and_the_right_buttons()
    {
        await WpfHost.RunAsync(() =>
        {
            var info = MessageDialog.Info("Título", "Mensagem de teste");
            var confirm = new MessageDialog("Reverter tudo", "Continuar?", "Reverter tudo", "Cancelar", isDanger: true);

            Assert.Equal("Título", info.Title);
            Assert.Equal(Visibility.Collapsed, info.CancelButton.Visibility);
            Assert.Equal("OK", info.ConfirmButton.Content);
            Assert.Equal(Visibility.Visible, confirm.CancelButton.Visibility);
            Assert.Equal("Cancelar", confirm.CancelButton.Content);
            Assert.Same(Application.Current.FindResource("DangerBtn"), confirm.ConfirmButton.Style);
            Assert.Same(Application.Current.FindResource("PrimaryBtn"), info.ConfirmButton.Style);

            SaveIfRequested(HostLikeWindow(confirm, 500), "dialogo-mensagem");
        });
    }

    // ---------- Conversores ----------

    [Fact]
    public void Converters_behave_as_documented()
    {
        var visibleWhenEmpty = new NullToVisibilityConverter();
        var visibleWhenFilled = new NullToVisibilityConverter { Invert = true };
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal(Visibility.Visible, visibleWhenEmpty.Convert(null, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, visibleWhenEmpty.Convert("", typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Collapsed, visibleWhenEmpty.Convert("texto", typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Collapsed, visibleWhenFilled.Convert(null, typeof(Visibility), null, culture));
        Assert.Equal(Visibility.Visible, visibleWhenFilled.Convert(new object(), typeof(Visibility), null, culture));

        Assert.Equal(1.0, new DimWhenFalseConverter().Convert(true, typeof(double), null, culture));
        Assert.Equal(0.55, new DimWhenFalseConverter().Convert(false, typeof(double), null, culture));
    }

    // ---------- Utilitários ----------

    private static T? FindFirst<T>(DependencyObject root)
        where T : DependencyObject => FindAll<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        // Run, Paragraph etc. fazem parte da árvore lógica mas não são elementos visuais.
        var count = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }

        // Elementos fora da árvore visual (conteúdo ainda não carregado) são alcançados pela árvore lógica.
        if (count == 0)
        {
            foreach (var logical in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (logical is T match)
                {
                    yield return match;
                }

                foreach (var nested in FindAll<T>(logical))
                {
                    yield return nested;
                }
            }
        }
    }
}
