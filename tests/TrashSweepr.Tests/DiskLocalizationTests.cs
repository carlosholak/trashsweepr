using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TrashSweepr.Tests.Helpers;

namespace TrashSweepr.Tests;

/// <summary>Seção Disco e janela do app em inglês e em português (a língua segue a interface do Windows).</summary>
public sealed partial class DiskLocalizationTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    [GeneratedRegex("[ãõçáéíóúâêôÃÕÇÁÉÍÓÚÂÊÔ]")]
    private static partial Regex PortugueseOnly();

    // ---------- Format: plurais e tempo relativo ----------

    [Fact]
    public void English_counts_use_singular_and_plural()
    {
        using (TestCulture.Use("en-US"))
        {
            Assert.Equal("1 file", Format.Files(1));
            Assert.Equal("0 files", Format.Files(0));
            Assert.Equal("1,234 files", Format.Files(1234));
            Assert.Equal("1 folder", Format.Plural(1, "Disk.Folders"));
            Assert.Equal("3 folders", Format.Plural(3, "Disk.Folders"));
            Assert.Equal("1.5 KB", Format.Bytes(1536));
            Assert.Equal("<0.1%", Format.Percent(0.0005));
            Assert.Equal("no change", Format.Signed(0));
        }
    }

    [Fact]
    public void Portuguese_counts_use_singular_and_plural()
    {
        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal("1 arquivo", Format.Files(1));
            Assert.Equal("1.234 arquivos", Format.Files(1234));
            Assert.Equal("1 pasta", Format.Plural(1, "Disk.Folders"));
            Assert.Equal("3 pastas", Format.Plural(3, "Disk.Folders"));
            Assert.Equal("1,5 KB", Format.Bytes(1536));
            Assert.Equal("<0,1%", Format.Percent(0.0005));
            Assert.Equal("sem mudança", Format.Signed(0));
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void Every_plural_key_has_singular_and_plural_forms(string culture)
    {
        // Format.Plural monta a chave (".One"/".Other"), então o teste de chaves usadas no código não as enxerga.
        string[] bases =
        [
            "Disk.Files", "Disk.Folders", "Disk.Moved", "Disk.DeniedFolders", "Disk.SmallerItems", "Disk.Recover.InProjects",
            "Disk.Ago.Minutes", "Disk.Ago.Hours", "Disk.Ago.Days", "Disk.Ago.Weeks", "Disk.Ago.Months", "Disk.Ago.Years",
        ];
        using (TestCulture.Use(culture))
        {
            foreach (var key in bases.SelectMany(b => new[] { b + ".One", b + ".Other" }))
            {
                var text = Localization.Loc.Get(key);
                Assert.False(text.StartsWith('!'), key);
                Assert.Contains("{0}", text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(1, "just now", "agora há pouco")]
    [InlineData(5, "5 min ago", "há 5 min")]
    [InlineData(60, "1 hour ago", "há 1 h")]
    [InlineData(180, "3 hours ago", "há 3 h")]
    [InlineData(60 * 24, "1 day ago", "há 1 dia")]
    [InlineData(60 * 24 * 3, "3 days ago", "há 3 dias")]
    [InlineData(60 * 24 * 20, "2 weeks ago", "há 2 semanas")]
    [InlineData(60 * 24 * 61, "2 months ago", "há 2 meses")]
    [InlineData(60 * 24 * 400, "1 year ago", "há 1 ano")]
    [InlineData(60 * 24 * 800, "2 years ago", "há 2 anos")]
    public void Relative_time_reads_naturally_in_both_languages(int minutesAgo, string english, string portuguese)
    {
        var then = Now.AddMinutes(-minutesAgo);
        using (TestCulture.Use("en-US"))
        {
            Assert.Equal(english, Format.Ago(then, Now));
        }

        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal(portuguese, Format.Ago(then, Now));
        }
    }

    [Fact]
    public void Dates_follow_the_regional_format()
    {
        var utc = new DateTime(2026, 3, 9, 15, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        using (TestCulture.Use("en-US"))
        {
            Assert.Equal(local.ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")), Format.Date(utc));
        }

        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal(local.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), Format.Date(utc));
        }
    }

    // ---------- Recriáveis ----------

    /// <summary>C:\proj com package.json (mexido há 3 dias), node_modules e .venv (com requirements.txt).</summary>
    private static (DiskNode Root, DiskNode NodeModules, DiskNode Venv) Project()
    {
        var root = new DiskNode(@"C:\proj", null, true) { Children = [] };
        var package = new DiskNode("package.json", root, false) { Size = 100, LastWriteTicks = Now.AddDays(-3).Ticks };
        var requirements = new DiskNode("requirements.txt", root, false) { Size = 10, LastWriteTicks = Now.AddDays(-3).Ticks };
        var nodeModules = new DiskNode("node_modules", root, true) { Children = [], Size = 5000 };
        var venv = new DiskNode(".venv", root, true) { Children = [], Size = 3000 };
        root.Children.AddRange([nodeModules, venv, package, requirements]);
        Recoverables.Mark(root);
        return (root, nodeModules, venv);
    }

    [Fact]
    public void Recoverable_descriptions_follow_the_interface_language_and_keep_commands()
    {
        var (root, nodeModules, venv) = Project();

        using (TestCulture.Use("en-US"))
        {
            Assert.Equal("npm dependencies", nodeModules.Recoverable!.Label);
            Assert.Equal("npm install", nodeModules.Recoverable.Command);
            Assert.Equal("Python virtual environment", venv.Recoverable!.Label);
            Assert.Equal("python -m venv .venv and pip install", venv.Recoverable.Command);
            Assert.Equal("npm dependencies · npm install · project last changed 3 days ago",
                new RecoverRow(nodeModules, root.FullPath, Now.Ticks).Detail);
        }

        // A mesma regra, já criada, muda de língua: os textos são montados na hora de mostrar.
        using (TestCulture.Use("pt-BR"))
        {
            Assert.Equal("Dependências npm", nodeModules.Recoverable.Label);
            Assert.Equal("npm install", nodeModules.Recoverable.Command);
            Assert.Equal("python -m venv .venv e pip install", venv.Recoverable.Command);
            Assert.Equal("Dependências npm · npm install · projeto mexido há 3 dias",
                new RecoverRow(nodeModules, root.FullPath, Now.Ticks).Detail);
        }
    }

    [Fact]
    public void Recycle_confirmation_is_fully_translated()
    {
        var (root, nodeModules, venv) = Project();

        using (TestCulture.Use("en-US"))
        {
            var text = MainWindow.RecycleConfirmation([nodeModules, venv], root.FullPath);
            Assert.StartsWith("Move 2 folders (7.8 KB) to the Recycle Bin?", text, StringComparison.Ordinal);
            Assert.Contains("node_modules  (4.9 KB) — npm install", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(PortugueseOnly(), text);

            Assert.StartsWith("Move 1 folder (", MainWindow.RecycleConfirmation([nodeModules], root.FullPath), StringComparison.Ordinal);
        }

        using (TestCulture.Use("pt-BR"))
        {
            Assert.StartsWith("Mover 2 pastas (7,8 KB) para a Lixeira?", MainWindow.RecycleConfirmation([nodeModules, venv], root.FullPath), StringComparison.Ordinal);
        }
    }

    // ---------- Janela ----------

    private static (MainWindow Window, Border Host) RenderWindow(double width, double height)
    {
        // A Limpeza não pode ser montada só por abrir a janela.
        var window = new MainWindow { CleanupFactory = () => throw new InvalidOperationException("Limpeza criada sem ser aberta") };
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new Border { Background = (Brush)window.FindResource("Bg"), Child = root };
        host.SetResourceReference(TextElement.ForegroundProperty, "Text");
        TextElement.SetFontFamily(host, window.FontFamily);
        TextElement.SetFontSize(host, window.FontSize);
        WpfHost.Layout(host, width, height);
        return (window, host);
    }

    /// <summary>Todo texto da janela (visível ou recolhido), menos a Limpeza e a lista de unidades (nomes dados pelo usuário).</summary>
    private static List<string> Texts(MainWindow window, DependencyObject root)
    {
        var texts = new List<string>();
        var seen = new HashSet<DependencyObject>();

        void Add(object? value)
        {
            if (value is string { Length: > 0 } s)
            {
                texts.Add(s);
            }
        }

        void Walk(DependencyObject node)
        {
            if (!seen.Add(node) || ReferenceEquals(node, window.CleanupPane) || ReferenceEquals(node, window.DriveBox))
            {
                return;
            }

            switch (node)
            {
                case TextBlock t:
                    Add(t.Text);
                    break;
                case HeaderedContentControl h:
                    Add(h.Header);
                    Add(h.Content);
                    break;
                case ContentControl c:
                    Add(c.Content);
                    break;
            }

            if (node is FrameworkElement fe)
            {
                Add(fe.ToolTip);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                Walk(child);
            }

            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        Walk(root);
        return texts;
    }

    [Fact]
    public async Task Main_window_disk_section_has_no_portuguese_text_in_english()
    {
        using (TestCulture.Use("en-US"))
        {
            await WpfHost.RunAsync(() =>
            {
                var (window, host) = RenderWindow(1320, 820);
                var texts = Texts(window, host);

                Assert.Contains("Disk", texts);
                Assert.Contains("Windows Cleanup", texts);
                Assert.Contains("Scan drive", texts);
                Assert.Contains("Rebuildable", texts);
                Assert.Contains("Only projects idle for 30+ days", texts); // aba recolhida também conta
                Assert.Empty(texts.Where(t => PortugueseOnly().IsMatch(t)));
            });
        }
    }

    [Fact]
    public async Task Main_window_disk_section_is_in_portuguese_on_a_portuguese_windows()
    {
        using (TestCulture.Use("pt-BR"))
        {
            await WpfHost.RunAsync(() =>
            {
                var (window, host) = RenderWindow(1320, 820);
                var texts = Texts(window, host);

                Assert.Contains("Limpeza do Windows", texts);
                Assert.Contains("Varrer unidade", texts);
                Assert.Contains("Só projetos parados há 30+ dias", texts);
            });
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task Side_panel_tabs_fit_on_one_line_at_the_default_size(string culture)
    {
        using (TestCulture.Use(culture))
        {
            await WpfHost.RunAsync(() =>
            {
                var (window, host) = RenderWindow(1320, 820);
                var tabs = new[] { window.ContentTab, window.TopTab, window.RecoverTab, window.ChangesTab };
                var tops = tabs.Select(t => t.TranslatePoint(new Point(0, 0), host).Y).Distinct().ToList();

                Assert.Single(tops);
                Assert.All(tabs, t => Assert.True(t.ActualWidth >= t.DesiredSize.Width - 0.5, $"{t.Content} cortada"));
            });
        }
    }
}
