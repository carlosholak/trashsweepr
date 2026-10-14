using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TrashSweepr.Limpeza;
using TrashSweepr.Limpeza.ViewModels;

namespace TrashSweepr;

public partial class MainWindow : Window
{
    const int StaleThresholdDays = 30;
    const int ConfirmListMax = 8; // pastas listadas por extenso na confirmação da Lixeira

    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly Stopwatch _stopwatch = new();

    // Comparação e análise leem a árvore numa thread do pool, uma de cada vez. Antes de mudar a árvore
    // (F5, Lixeira) a thread da interface espera as duas terminarem: ver WaitTreeReaders.
    readonly object _treeReadLock = new();
    Task _baselineTask = Task.CompletedTask;
    Task _analysisTask = Task.CompletedTask;
    CancellationTokenSource? _cts;
    Scanner? _scanner;
    string _scanTarget = "";
    bool _recycling;

    DiskNode? _root;
    DiskNode? _current;
    SnapNode? _baseline;
    SnapshotInfo? _baselineInfo;
    int _baselineVersion;
    bool _suppressBaselineEvent;

    int _panelVersion;
    Analysis? _analysis;
    List<Row> _contentRows = [];
    List<Row> _topRows = [];
    List<RecoverRow> _recoverRows = [];
    List<ChangeRow> _changeRows = [];
    bool _syncingSelection;

    // Seção "Limpeza do Windows": só é montada na primeira vez que o usuário a abre.
    LimpezaViewModel? _limpeza;
    IDisposable? _limpezaRuntime;

    /// <summary>Cria a Limpeza do Windows. Os testes trocam isto para não consultar o sistema de verdade.</summary>
    internal Func<(LimpezaViewModel ViewModel, IDisposable Runtime)> LimpezaFactory { get; set; } = LimpezaComposition.Create;

    public MainWindow()
    {
        InitializeComponent();
        StaleButton.Content = $"Só projetos parados há {StaleThresholdDays}+ dias";
        LoadDrives();
        Closed += (_, _) => _limpezaRuntime?.Dispose();
        _progressTimer.Tick += (_, _) => ShowProgress();
        Map.HoverChanged += OnMapHover;
        Map.ItemClicked += OnMapClick;
        Map.ItemDoubleClicked += node => Enter(node);

        // "trashsweepr.exe <pasta>" já abre varrendo a pasta (também vale arrastar uma pasta sobre o .exe).
        // "--aba recriavel|mudancas|maiores" abre direto numa aba.
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var folder = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a));
        int tabIndex = args.IndexOf("--aba");
        string? tab = tabIndex >= 0 && tabIndex + 1 < args.Count ? args[tabIndex + 1] : null;

        // "--limpeza" abre direto na seção Limpeza do Windows (é o que o app usa ao reabrir como administrador).
        if (args.Contains("--limpeza")) Loaded += (_, _) => CleanupSection.IsChecked = true;
        if (folder is not null)
            Loaded += async (_, _) =>
            {
                await ScanRoot(Path.GetFullPath(folder));
                var radio = tab switch
                {
                    "recriavel" => RecoverTab,
                    "mudancas" => ChangesTab,
                    "maiores" => TopTab,
                    _ => null,
                };
                if (radio is not null) radio.IsChecked = true;
            };
    }

    bool Busy => _cts is not null || _recycling;

    // ---------- Seções ----------

    void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        // O evento Checked já dispara durante o InitializeComponent, antes dos painéis existirem.
        if (DiskPane is null || LimpezaPane is null) return;

        bool cleanup = CleanupSection.IsChecked == true;
        DiskPane.Visibility = cleanup ? Visibility.Collapsed : Visibility.Visible;
        LimpezaPane.Visibility = cleanup ? Visibility.Visible : Visibility.Collapsed;
        if (cleanup) EnsureLimpezaStarted();
    }

    void EnsureLimpezaStarted()
    {
        if (_limpeza is not null) return;

        (_limpeza, _limpezaRuntime) = LimpezaFactory();
        LimpezaPane.DataContext = _limpeza;
        _ = InitializeLimpezaAsync(_limpeza);
    }

    async Task InitializeLimpezaAsync(LimpezaViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Limpeza do Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    ColorMode Mode => (ColorMode)Math.Max(0, ModeBox.SelectedIndex);

    sealed record DriveItem(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Lista as unidades (com o espaço livre atual), mantendo a que estava escolhida.</summary>
    void LoadDrives()
    {
        string? previous = (DriveBox.SelectedItem as DriveItem)?.Path;
        var items = new List<DriveItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                string kind = d.DriveType switch
                {
                    DriveType.Fixed => "Local",
                    DriveType.Removable => "Removível",
                    DriveType.Network => "Rede",
                    DriveType.CDRom => "Óptica",
                    _ => d.DriveType.ToString(),
                };
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? kind : $"{d.VolumeLabel} · {kind}";
                items.Add(new DriveItem(d.RootDirectory.FullName,
                    $"{d.Name}   {label}   {Format.Bytes(d.AvailableFreeSpace)} livres de {Format.Bytes(d.TotalSize)}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        DriveBox.ItemsSource = items;
        int keep = items.FindIndex(i => string.Equals(i.Path, previous, StringComparison.OrdinalIgnoreCase));
        DriveBox.SelectedIndex = keep >= 0 ? keep : items.Count > 0 ? 0 : -1;
    }

    // ---------- Varredura ----------

    void OnScanDrive(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is DriveItem d) _ = ScanRoot(d.Path);
    }

    void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta para mapear" };
        if (dlg.ShowDialog(this) == true) _ = ScanRoot(dlg.FolderName);
    }

    void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    async Task ScanRoot(string path)
    {
        // A data do registro é a do início: o que for modificado durante a varredura conta como depois dela.
        var startedUtc = DateTime.UtcNow;
        List<SnapshotInfo> previous = [];
        string? saveError = null;
        var result = await RunScan(path, $"Varrendo {path}", root =>
        {
            Recoverables.Mark(root);
            // Lista depois de gravar: a gravação apaga os registros mais antigos, e nenhum apagado pode
            // continuar oferecido na comparação. Erro na pasta de registros não descarta a varredura.
            try
            {
                Snapshots.Save(root, startedUtc);
                previous = Snapshots.List(root.Name).Where(s => s.TakenUtc != startedUtc).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveError = ex.Message; }
        });
        if (result is null) return;

        _root = result;
        _baseline = null;
        _baselineInfo = null;
        Navigate(result);
        if (saveError is not null) ScanInfo.Text += " · não foi possível gravar ou ler os registros para comparação";

        _suppressBaselineEvent = true;
        BaselineBox.ItemsSource = previous;
        BaselineBox.SelectedIndex = previous.Count > 0 ? 0 : -1;
        _suppressBaselineEvent = false;
        await LoadBaseline(previous.FirstOrDefault());
    }

    void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshCurrent();

    /// <summary>Varre de novo só a pasta aberta e encaixa o resultado na árvore existente.</summary>
    async Task RefreshCurrent()
    {
        if (_current is not { } node || Busy) return;
        await RunScan(node.FullPath, $"Atualizando {node.FullPath}", applyOnUi: fresh =>
        {
            Splice(node, fresh);
            Navigate(node);
        });
    }

    /// <summary>Troca o conteúdo de <paramref name="node"/> pelo de uma varredura nova e corrige os totais acima.</summary>
    void Splice(DiskNode node, DiskNode fresh)
    {
        long deltaSize = fresh.Size - node.Size;
        int deltaCount = fresh.FileCount - node.FileCount;
        foreach (var c in fresh.Children!) c.Parent = node;
        node.Children = fresh.Children;
        node.Size = fresh.Size;
        node.FileCount = fresh.FileCount;
        node.LastWriteTicks = fresh.LastWriteTicks;
        node.AccessDenied = fresh.AccessDenied;
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            p.Size += deltaSize;
            p.FileCount += deltaCount;
            p.Children!.Sort(DiskNode.BySizeDescending);
        }

        // As regras olham o pai, então marca a partir dele; sem pai (raiz) marca a própria pasta.
        Recoverables.Mark(node.Parent ?? node);
        if (_baseline is not null && _baselineInfo is not null)
            Snapshots.Compare(node, Snapshots.Find(_baseline, node), _baselineInfo.TakenUtc);
    }

    /// <param name="afterScan">Roda ainda na thread da varredura, com a árvore nova.</param>
    /// <param name="applyOnUi">
    /// Encaixa o resultado na árvore atual, na thread da interface e ainda ocupado: espera antes as leituras
    /// em segundo plano, para nada mudar a árvore enquanto outra thread a percorre.
    /// </param>
    async Task<DiskNode?> RunScan(string path, string title, Action<DiskNode>? afterScan = null,
        Action<DiskNode>? applyOnUi = null)
    {
        if (Busy) return null;
        _cts = new CancellationTokenSource();
        _scanner = new Scanner();
        _scanTarget = title;
        SetBusy(true);
        _stopwatch.Restart();
        _progressTimer.Start();
        ShowProgress();
        try
        {
            var scanner = _scanner;
            var token = _cts.Token;
            var root = await Task.Run(() =>
            {
                var r = scanner.Scan(path, token);
                afterScan?.Invoke(r);
                return r;
            });
            _stopwatch.Stop();
            string denied = scanner.DeniedFolders > 0
                ? $" · {Format.Plural(scanner.DeniedFolders, "pasta sem permissão ignorada", "pastas sem permissão ignoradas")}"
                : "";
            ScanInfo.Text = $"Varredura em {Format.Seconds(_stopwatch.Elapsed)} · {Format.Files(scanner.Files)}{denied}";
            if (applyOnUi is not null)
            {
                await WaitTreeReaders();
                applyOnUi(root);
            }
            return root;
        }
        catch (Exception ex) when (Scanner.IsCancellation(ex))
        {
            ScanInfo.Text = "Varredura cancelada";
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível varrer a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            _progressTimer.Stop();
            _cts.Dispose();
            _cts = null;
            _scanner = null;
            SetBusy(false);
        }
    }

    void SetBusy(bool busy)
    {
        // A comparação percorre a árvore em segundo plano: não pode começar enquanto ela muda.
        ScanButton.IsEnabled = FolderButton.IsEnabled = DriveBox.IsEnabled = BaselineBox.IsEnabled = !busy;
        CancelButton.Visibility = busy && !_recycling ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy && _current is not null;
        UpButton.IsEnabled = !busy && _current?.Parent is not null;
        OverlayProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecycleButton();
        if (busy)
        {
            Overlay.Visibility = Visibility.Visible;
        }
        else if (_current is null)
        {
            // Cancelou sem nada carregado: volta para o estado inicial.
            OverlayTitle.Text = "Descubra o que está ocupando espaço";
            OverlayText.Text = "Escolha uma unidade e clique em Varrer unidade, ou abra uma pasta específica. " +
                               "Pastas de build e cache que dá para recriar podem ir para a Lixeira.";
            OverlayDetail.Text = "";
        }
        else
        {
            Overlay.Visibility = Visibility.Collapsed;
        }
    }

    void ShowProgress()
    {
        if (_scanner is not { } s) return;
        OverlayTitle.Text = _scanTarget;
        OverlayText.Text = $"{Format.Files(s.Files)} · {Format.Bytes(s.Bytes)} · {Format.Seconds(_stopwatch.Elapsed)}";
        OverlayDetail.Text = s.CurrentPath;
    }

    // ---------- Comparação com varreduras anteriores ----------

    void OnBaselineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressBaselineEvent) _ = LoadBaseline(BaselineBox.SelectedItem as SnapshotInfo);
    }

    async Task LoadBaseline(SnapshotInfo? info)
    {
        if (_root is not { } root) return;
        int version = ++_baselineVersion;
        SnapNode? snap = null;
        string? error = null;
        // Uma comparação mais antiga ainda em curso segura o lock; esta só termina depois dela,
        // então esperar a última basta (WaitTreeReaders).
        var task = Task.Run(() =>
        {
            lock (_treeReadLock)
            {
                if (version != Volatile.Read(ref _baselineVersion)) return;
                try
                {
                    if (info is null) Snapshots.Clear(root);
                    else
                    {
                        snap = Snapshots.Load(info.FilePath);
                        Snapshots.Compare(root, snap, info.TakenUtc);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                    snap = null;
                    Snapshots.Clear(root);
                }
            }
        });
        _baselineTask = task;
        await task;
        if (version != _baselineVersion) return;
        _baseline = snap;
        _baselineInfo = snap is null ? null : info;
        if (error is not null) ScanInfo.Text = "Não foi possível ler a varredura anterior: " + error;
        if (_current is null) return;
        Map.Redraw(); // os tamanhos de referência mudaram (modo Mudanças)
        RefreshPanels(keepScroll: true);
    }

    /// <summary>
    /// Espera a comparação e a análise em segundo plano terminarem. Só depois disto a thread da interface
    /// pode mudar a árvore, sem novo await até a mudança acabar (senão outra leitura pode começar no meio).
    /// </summary>
    async Task WaitTreeReaders()
    {
        while (!_baselineTask.IsCompleted || !_analysisTask.IsCompleted)
        {
            try { await Task.WhenAll(_baselineTask, _analysisTask); }
            catch (Exception) { } // quem iniciou cada tarefa já trata o erro dela
        }
    }

    // ---------- Navegação ----------

    void Navigate(DiskNode node)
    {
        _current = node;
        Map.Root = node;
        Overlay.Visibility = Visibility.Collapsed;
        UpButton.IsEnabled = node.Parent is not null;
        RefreshButton.IsEnabled = true;

        var chain = new List<DiskNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        Crumbs.ItemsSource = chain.Select((n, i) => new Crumb(n, n.Name, i > 0)).ToList();
        RefreshPanels(keepScroll: false);
    }

    /// <summary>
    /// Refaz lista e painéis da pasta atual (depois de navegar, comparar ou limpar). O mapa não: trocar
    /// <see cref="TreemapControl.Root"/> já o redesenha, e quem muda a árvore sem navegar chama Map.Redraw.
    /// </summary>
    void RefreshPanels(bool keepScroll)
    {
        var node = _current!;
        string denied = node.AccessDenied ? " · sem permissão para ler esta pasta" : "";
        CurrentSummary.Text = $"{Format.Bytes(node.Size)} · {Format.Files(node.FileCount)}{denied}";

        _contentRows = node.Children!.Where(c => c.Size > 0 || c.IsDirectory).Select(c => new Row(c, node.Size)).ToList();
        _changeRows = BuildChangeRows(node);
        UpdateChangeSummary(node);
        ShowRows(keepScroll);
        _ = UpdateSidePanels(node);
    }

    /// <summary>Entra na pasta mais funda do item clicado (ou na pasta dele, se for arquivo).</summary>
    void Enter(DiskNode node)
    {
        if (Busy || EnterTarget(node) is not { } target) return;
        Navigate(target);
    }

    /// <summary>A pasta em que dá para entrar a partir do item, ou null (já aberta, atalho ou vazia).</summary>
    DiskNode? EnterTarget(DiskNode node)
    {
        var target = node.IsDirectory && !node.IsAggregate ? node : node.Parent;
        if (target is null || target == _current || target.IsLink || target.Children is not { Count: > 0 }) return null;
        return target;
    }

    void OnUp(object sender, RoutedEventArgs e) => GoUp();

    void GoUp()
    {
        if (_current?.Parent is not { } parent || Busy) return;
        var from = _current;
        Navigate(parent);
        SelectNode(from);
    }

    void OnCrumb(object sender, RoutedEventArgs e)
    {
        if (!Busy && sender is FrameworkElement { Tag: DiskNode node } && node != _current) Navigate(node);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Atalhos do mapa (F5, Backspace, Esc) não valem na Limpeza: Backspace é para apagar texto na busca.
        if (CleanupSection.IsChecked == true) return;

        if (e.Key == Key.F5)
        {
            _ = RefreshCurrent();
            e.Handled = true;
        }
        // Com Alt pressionado o WPF manda Key.System e a tecla real em SystemKey.
        else if (e.Key == Key.Back || (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            GoUp();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _cts is not null)
        {
            _cts.Cancel();
            e.Handled = true;
        }
    }

    // ---------- Modos de cor ----------

    void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Map.Mode = Mode;
        RenderLegend();
    }

    void SetMode(ColorMode mode)
    {
        if (Mode != mode) ModeBox.SelectedIndex = (int)mode;
    }

    // ---------- Painel lateral ----------

    async Task UpdateSidePanels(DiskNode node)
    {
        int version = ++_panelVersion;
        long now = DateTime.UtcNow.Ticks;
        // Mesmo esquema da comparação: uma análise antiga ou já superada não percorre a árvore depois desta.
        var task = Task.Run<Analysis?>(() =>
        {
            lock (_treeReadLock)
                return version == Volatile.Read(ref _panelVersion) ? DiskAnalysis.Analyze(node, now) : null;
        });
        _analysisTask = task;
        var analysis = await task;
        if (analysis is null || version != _panelVersion) return;

        _analysis = analysis;
        string basePath = node.FullPath;
        _topRows = analysis.Top
            .Select(f => new Row(f, node.Size, Format.RelativePath(basePath, f.Parent?.FullPath ?? "", "nesta pasta")))
            .ToList();

        var previouslyChecked = _recoverRows.Where(r => r.IsChecked).Select(r => r.Node).ToHashSet();
        foreach (var r in _recoverRows) r.PropertyChanged -= OnRecoverRowChanged;
        _recoverRows = analysis.Recoverable.Select(n => new RecoverRow(n, basePath, now)).ToList();
        foreach (var r in _recoverRows)
        {
            r.IsChecked = previouslyChecked.Contains(r.Node);
            r.PropertyChanged += OnRecoverRowChanged;
        }
        UpdateRecoverSummary();
        RenderLegend();
        if (TopTab.IsChecked == true || RecoverTab.IsChecked == true) ShowRows(keepScroll: true);
    }

    void RenderLegend()
    {
        LegendNote.Visibility = Visibility.Collapsed;
        if (_analysis is not { } a)
        {
            Legend.ItemsSource = null;
            return;
        }
        var rows = new List<LegendRow>();
        switch (Mode)
        {
            case ColorMode.Idade:
            {
                LegendTitle.Text = "Por idade (última modificação)";
                long total = a.ByAge.Sum();
                foreach (var b in Coloring.AgeBuckets)
                    if (a.ByAge[(int)b] > 0) rows.Add(new LegendRow(Coloring.Brush(b), Coloring.Label(b), a.ByAge[(int)b], total));
                break;
            }
            case ColorMode.Recriavel:
            {
                LegendTitle.Text = "Recriável nesta pasta";
                long total = a.Node.Size;
                rows.Add(new LegendRow(Coloring.Recoverable, "Build e cache recriáveis", a.RecoverableSize, total));
                rows.Add(new LegendRow(Coloring.Neutral, "Restante", Math.Max(0, total - a.RecoverableSize), total));
                break;
            }
            case ColorMode.Mudancas:
            {
                if (_baselineInfo is null)
                {
                    LegendTitle.Text = "Mudanças";
                    ShowLegendNote("Ainda não há varredura anterior desta pasta. A comparação aparece a partir da próxima varredura.");
                    break;
                }
                LegendTitle.Text = $"Mudanças desde {Format.Date(_baselineInfo.TakenUtc)}";
                long total = Coloring.ChangeKinds.Sum(k => a.ByChange[(int)k]);
                foreach (var k in Coloring.ChangeKinds)
                    if (a.ByChange[(int)k] > 0) rows.Add(new LegendRow(Coloring.Brush(k), Coloring.Label(k), a.ByChange[(int)k], total));
                break;
            }
            default:
            {
                LegendTitle.Text = "Por tipo de arquivo";
                long total = a.ByCategory.Sum();
                rows.AddRange(FileCategories.All
                    .Where(c => a.ByCategory[(int)c] > 0)
                    .OrderByDescending(c => a.ByCategory[(int)c])
                    .Select(c => new LegendRow(FileCategories.Brush(c), FileCategories.Label(c), a.ByCategory[(int)c], total)));
                break;
            }
        }
        Legend.ItemsSource = rows;
    }

    void ShowLegendNote(string text)
    {
        LegendNote.Text = text;
        LegendNote.Visibility = Visibility.Visible;
    }

    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // As abas novas mostram melhor o mapa na cor correspondente.
        if (RecoverTab.IsChecked == true) SetMode(ColorMode.Recriavel);
        else if (ChangesTab.IsChecked == true) SetMode(ColorMode.Mudancas);
        ShowRows(keepScroll: false);
    }

    void ShowRows(bool keepScroll)
    {
        System.Collections.IList rows;
        string empty;
        if (TopTab.IsChecked == true)
        {
            rows = _topRows;
            empty = "Nenhum arquivo nesta pasta.";
        }
        else if (RecoverTab.IsChecked == true)
        {
            rows = _recoverRows;
            empty = _current?.Parent?.RecoverableAncestor() is { } owner
                ? $"Esta pasta já está dentro de {owner.Name}, que é recriável inteira."
                : "Nada de build ou cache recriável foi encontrado nesta pasta.";
        }
        else if (ChangesTab.IsChecked == true)
        {
            rows = _changeRows;
            empty = _baselineInfo is null
                ? "A comparação aparece a partir da segunda varredura desta pasta."
                : "Nada mudou nesta pasta desde a varredura escolhida.";
        }
        else
        {
            rows = _contentRows;
            empty = "Pasta vazia.";
        }

        RecoverHeader.Visibility = RecoverTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChangesHeader.Visibility = ChangesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (!ReferenceEquals(ItemsList.ItemsSource, rows) || !keepScroll)
        {
            ItemsList.ItemsSource = rows;
            if (!keepScroll && ItemsList.Items.Count > 0) ItemsList.ScrollIntoView(ItemsList.Items[0]);
        }
        EmptyText.Text = empty;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    List<ChangeRow> BuildChangeRows(DiskNode node)
    {
        var rows = new List<ChangeRow>();
        if (_baselineInfo is null) return rows;
        foreach (var c in node.Children!)
        {
            var kind = Coloring.Change(c);
            if (kind is ChangeKind.Novo or ChangeKind.Cresceu or ChangeKind.Diminuiu && Coloring.Delta(c) != 0)
                rows.Add(new ChangeRow(c));
        }
        if (node.Removed is { } removed)
            foreach (var (name, size, isDir) in removed)
                if (size > 0) rows.Add(new ChangeRow(name, size, isDir));
        rows.Sort((a, b) => Math.Abs(b.Delta).CompareTo(Math.Abs(a.Delta)));
        return rows;
    }

    void UpdateChangeSummary(DiskNode node)
    {
        if (_baselineInfo is null)
        {
            ChangeSummary.Text = BaselineBox.Items.Count == 0
                ? "Primeira varredura desta pasta. O registro foi guardado; na próxima varredura dá para ver o que mudou."
                : "Escolha uma varredura anterior para comparar.";
            return;
        }
        string since = Format.Ago(_baselineInfo.TakenUtc, DateTime.UtcNow);
        ChangeSummary.Text = node.PreviousSize switch
        {
            DiskNode.Added => $"Esta pasta não existia na varredura de {Format.Date(_baselineInfo.TakenUtc)}.",
            DiskNode.NoBaseline => "",
            var prev when prev == node.Size => $"Nada mudou no tamanho desta pasta desde {Format.Date(_baselineInfo.TakenUtc)} ({since}).",
            var prev => $"{Format.Signed(node.Size - prev)} nesta pasta ({since}): era {Format.Bytes(prev)}, agora {Format.Bytes(node.Size)}.",
        };
    }

    void SelectNode(DiskNode? node)
    {
        _syncingSelection = true;
        ItemsList.SelectedItem = node is null ? null
            : ItemsList.ItemsSource?.Cast<object>().FirstOrDefault(r => r is INodeRow nr && nr.Node == node);
        if (ItemsList.SelectedItem is not null) ItemsList.ScrollIntoView(ItemsList.SelectedItem);
        _syncingSelection = false;
        Map.Selected = node;
    }

    void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        Map.Selected = (ItemsList.SelectedItem as INodeRow)?.Node;
    }

    void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Busy || RowUnder(e)?.Node is not { } node) return;
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) is not null) return;

        if (ContentTab.IsChecked == true && node.IsDirectory)
        {
            Enter(node);
        }
        else if (node.Parent is { } folder)
        {
            // Nas outras abas, abre a pasta onde o item está e o deixa selecionado.
            Navigate(folder);
            ContentTab.IsChecked = true;
            SelectNode(node);
        }
    }

    void OnListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e)?.Node is { } node) ShowMenu(node, ItemsList);
    }

    static INodeRow? RowUnder(MouseEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext as INodeRow;

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    // ---------- Recriáveis e Lixeira ----------

    void OnRecoverRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateRecycleButton();

    void UpdateRecoverSummary()
    {
        if (_analysis is not { } a || a.Recoverable.Count == 0)
        {
            RecoverSummary.Text = "Nenhuma pasta de build ou cache recriável aqui.";
            return;
        }
        int projects = a.Recoverable.Where(n => n.Recoverable!.IsProject).Select(n => n.Parent).Distinct().Count();
        string where = projects > 0 ? $" em {Format.Plural(projects, "projeto", "projetos")}" : "";
        string share = a.Node.Size > 0 ? $" ({Format.Percent((double)a.RecoverableSize / a.Node.Size)} desta pasta)" : "";
        RecoverSummary.Text = $"{Format.Bytes(a.RecoverableSize)} recriáveis{share}{where}. " +
                              "Cada pasta volta com o comando indicado; marque as que quer liberar.";
    }

    void UpdateRecycleButton()
    {
        var marked = _recoverRows.Where(r => r.IsChecked).ToList();
        RecycleButton.IsEnabled = !Busy && marked.Count > 0;
        RecycleButton.Content = marked.Count == 0
            ? "Mover para a Lixeira"
            : $"Mover {Format.Plural(marked.Count, "pasta", "pastas")} para a Lixeira · {Format.Bytes(marked.Sum(r => r.Node!.Size))}";
    }

    void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _recoverRows) r.IsChecked = true;
    }

    void OnUncheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _recoverRows) r.IsChecked = false;
    }

    void OnCheckStale(object sender, RoutedEventArgs e)
    {
        foreach (var r in _recoverRows) r.IsChecked = r.StaleDays >= StaleThresholdDays;
    }

    void OnRecycleChecked(object sender, RoutedEventArgs e) =>
        _ = Recycle(_recoverRows.Where(r => r.IsChecked).Select(r => r.Node!).ToList());

    async Task Recycle(List<DiskNode> nodes)
    {
        if (Busy || nodes.Count == 0) return;
        string basePath = _current?.FullPath ?? nodes[0].Parent!.FullPath;
        if (MessageBox.Show(this, RecycleConfirmation(nodes, basePath), "Mover para a Lixeira", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _recycling = true;
        SetBusy(true);
        var hwnd = new WindowInteropHelper(this).Handle;
        int moved = 0, cancelled = 0, failed = 0, tooLong = 0;
        long freed = 0;
        try
        {
            foreach (var n in nodes)
            {
                string path = n.FullPath;
                OverlayTitle.Text = "Movendo para a Lixeira";
                OverlayText.Text = $"{moved + cancelled + failed + tooLong + 1} de {nodes.Count} · {Format.Bytes(n.Size)}";
                OverlayDetail.Text = path;

                // O shell não aceita caminhos longos: nem tenta, e a pasta fica como está.
                if (RecycleBin.IsTooLong(path))
                {
                    tooLong++;
                    continue;
                }

                int code;
                try { code = await RecycleBin.SendAsync(path, hwnd); }
                catch (Exception) { code = -1; }

                if (!Directory.Exists(path))
                {
                    moved++;
                    freed += n.Size;
                    await WaitTreeReaders();
                    RemoveNode(n);
                }
                else
                {
                    // Cancelado ou parcial: varre de novo para os números baterem com o disco.
                    if (RecycleBin.IsCancelled(code)) cancelled++;
                    else failed++;
                    var fresh = await Task.Run(() => new Scanner().Scan(path, CancellationToken.None));
                    await WaitTreeReaders();
                    Splice(n, fresh);
                }
            }
        }
        finally
        {
            _recycling = false;
            SetBusy(false);
        }

        foreach (var r in _recoverRows) r.IsChecked = false;
        var notMoved = new List<string>();
        if (failed > 0) notMoved.Add($"{failed} em uso ou sem permissão");
        if (cancelled > 0) notMoved.Add($"{cancelled} canceladas");
        if (tooLong > 0) notMoved.Add($"{tooLong} com caminho longo demais para a Lixeira");
        ScanInfo.Text = notMoved.Count == 0
            ? $"{Format.Plural(moved, "pasta movida", "pastas movidas")} para a Lixeira · {Format.Bytes(freed)} liberados"
            : $"{moved} movidas ({Format.Bytes(freed)}) · não foram movidas: {string.Join(", ", notMoved)}";
        LoadDrives();
        if (_current is null) return;
        Map.Redraw(); // a árvore mudou sem trocar a pasta aberta
        RefreshPanels(keepScroll: true);
    }

    /// <summary>Texto da confirmação antes de mandar pastas para a Lixeira.</summary>
    internal static string RecycleConfirmation(IReadOnlyList<DiskNode> nodes, string basePath)
    {
        long total = nodes.Sum(n => n.Size);
        var lines = nodes.Take(ConfirmListMax).Select(n =>
            $"•  {Format.RelativePath(basePath, n.FullPath, n.FullPath)}  ({Format.Bytes(n.Size)}) — {n.Recoverable!.Command}");
        string more = nodes.Count > ConfirmListMax ? $"\n…e mais {nodes.Count - ConfirmListMax}." : "";
        return
            $"Mover {Format.Plural(nodes.Count, "pasta", "pastas")} ({Format.Bytes(total)}) para a Lixeira?\n\n" +
            string.Join("\n", lines) + more +
            "\n\nDá para restaurar pela Lixeira do Windows. Se algo não couber nela, o Windows avisa antes de apagar de vez.";
    }

    /// <summary>Tira um item da árvore e desconta o tamanho dele de todas as pastas acima.</summary>
    void RemoveNode(DiskNode node)
    {
        if (node.Parent is not { } parent) return;
        parent.Children!.Remove(node);
        if (node.PreviousSize >= 0)
            (parent.Removed ??= []).Add((node.Name, node.PreviousSize, node.IsDirectory));
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= node.Size;
            p.FileCount -= node.FileCount;
            p.Parent?.Children!.Sort(DiskNode.BySizeDescending);
        }
        if (_current is not null && _current.IsInside(node)) Navigate(parent);
    }

    // ---------- Mapa ----------

    void OnMapHover(DiskNode? node)
    {
        if (node is null)
        {
            HoverInfo.Text = "";
            return;
        }
        string share = _current is { Size: > 0 } cur ? $" · {Format.Percent((double)node.Size / cur.Size)} desta pasta" : "";
        string what = node.IsAggregate ? node.Name : node.FullPath;
        string extra = Mode switch
        {
            ColorMode.Idade when !node.IsAggregate =>
                $" · modificado {Format.Ago(new DateTime(node.LastWriteTicks, DateTimeKind.Utc), DateTime.UtcNow)}",
            ColorMode.Recriavel => node.RecoverableAncestor() is { Recoverable: { } r }
                ? $" · recriável: {r.Label.ToLowerInvariant()} ({r.Command})"
                : " · não recriável",
            ColorMode.Mudancas => Coloring.Change(node) switch
            {
                ChangeKind.Novo => " · novo",
                ChangeKind.Cresceu or ChangeKind.Diminuiu => $" · {Format.Signed(Coloring.Delta(node))} (era {Format.Bytes(node.PreviousSize)})",
                ChangeKind.Igual => " · sem mudança",
                _ => "",
            },
            _ => node.IsDirectory ? $" · {Format.Files(node.FileCount)}" : "",
        };
        HoverInfo.Text = $"{what}   {Format.Bytes(node.Size)}{share}{extra}";
    }

    void OnMapClick(DiskNode node) => SelectNode(node);

    void OnMapRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Map.HitNode(e.GetPosition(Map)) is { } node) ShowMenu(node, Map);
    }

    void ShowMenu(DiskNode node, UIElement target)
    {
        if (node.IsAggregate)
            node = node.Parent!;

        var menu = new ContextMenu { PlacementTarget = target };
        if (EnterTarget(node) is not null)
            menu.Items.Add(Item(node.IsDirectory ? "Entrar na pasta" : "Entrar na pasta do arquivo", () => Enter(node)));
        menu.Items.Add(Item("Mostrar no Explorer", () => ShowInExplorer(node)));
        menu.Items.Add(Item("Copiar caminho", () => CopyPath(node)));

        // Só pastas reconhecidas como recriáveis podem ir para a Lixeira por aqui.
        if (node.RecoverableAncestor() is { Parent: not null } rec && !Busy)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Mover {rec.Name} para a Lixeira… ({Format.Bytes(rec.Size)}, recriável)", () => _ = Recycle([rec])));
        }
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void CopyPath(DiskNode node)
    {
        // A área de transferência pode estar presa por outro programa (CLIPBRD_E_CANT_OPEN).
        try { Clipboard.SetText(node.FullPath); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível copiar o caminho", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowInExplorer(DiskNode node)
    {
        string path = node.FullPath;
        try
        {
            if (node.Parent is null) Process.Start("explorer.exe", $"\"{path}\"");
            else Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível abrir o Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
