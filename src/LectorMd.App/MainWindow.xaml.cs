using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Azure.Identity;
using LectorMd.App.Controls;
using LectorMd.App.Services;
using LectorMd.Core;
using LectorMd.Core.Model;
using LectorMd.Core.Planning;
using LectorMd.Core.Settings;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;
using Microsoft.Win32;

namespace LectorMd.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppPaths _paths = AppPaths.Default;
    private readonly SettingsStore _settingsStore;
    private readonly ReadAloudController _player = new();
    private readonly ObservableCollection<DocumentTab> _tabs = [];
    private readonly AzureAccount _azureAccount;
    private AppSettings _settings;
    private EngineServices? _services;
    private DocumentView? _view;
    private SpeechDocument? _document;
    private DocumentTab? _active;
    private IReadOnlyList<OutlineEntry> _outline = [];
    private IReadOnlyList<SpeechChunk> _chunks = [];
    private TimeSpan _lastElapsed; // última posición mostrada en la línea de tiempo (base para −10 s / +10 s)
    private ReadingTimeline? _timeline;
    private int _activation;    // cada activación de pestaña invalida las que todavía estaban cargando
    private int _shownActivation; // activación cuyo documento ya está en pantalla
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey; // Ctrl+Alt+L: leer el portapapeles desde cualquier app
    private MiniPlayerWindow? _mini; // solo existe mientras la ventana está oculta o minimizada

    /// <summary>Sonando o preparando el audio (no en pausa ni detenido).</summary>
    private bool IsReading => _player.State is PlaybackState.Playing or PlaybackState.Loading;
    private bool _exiting;        // true = cerrar termina la app (si no, la ventana se oculta en la bandeja)
    private bool _syncingTabs;  // la selección de la barra la está cambiando el código, no el usuario
    private readonly string? _fileFromCommandLine;
    private string? _lastFallbackReason;
    private (long Characters, DateTime At)? _azureReport;

    public MainWindow(string? fileToOpen)
    {
        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();
        ThemeService.Apply(_settings.Theme); // antes de crear los controles, para que no parpadeen
        ThemeService.WatchSystemTheme();
        ThemeService.Changed += UpdateThemeButton;
        Activated += (_, _) => _mini?.Close(); // la ventana a la vista: el mini reproductor sobra
        InitializeComponent();
        _fileFromCommandLine = fileToOpen;
        _azureAccount = new AzureAccount(_paths);
        TabList.ItemsSource = _tabs;

        _player.SegmentChanged += OnSegmentChanged;
        _player.StateChanged += OnPlaybackStateChanged;
        _player.ChunkStarted += OnChunkStarted;
        _player.DocumentFinished += OnDocumentFinished;
        _player.Failed += OnPlaybackFailed;
        _player.AudioDeviceLost += () =>
            ShowStatus("Se perdió la salida de audio (se desconectó o cambió). Quedó en pausa: pulsa Espacio para seguir desde el párrafo donde ibas.");

        _player.ProgressChanged += OnProgressChanged;

        Player.PlayPauseClicked += TogglePlay;
        Player.StopClicked += () => _player.Stop();
        Player.PreviousClicked += () => _player.PreviousSection();
        Player.NextClicked += () => _player.NextSection();
        Player.SpeedChanged += OnSpeedChanged;
        Player.RefreshUsageClicked += () => _ = RefreshAzureUsageAsync();
        Player.ChapterClicked += OnChapterClicked;
        Player.SkipRequested += SkipBy;
        Player.EngineChoiceRequested += OnEngineChoiceRequested;

        // Una velocidad del deslizador anterior (p. ej. 1.15) pasa al paso más cercano del selector.
        _settings.PlaybackSpeed = PlaybackSpeeds.Nearest(_settings.PlaybackSpeed);
        Player.SetSpeed(_settings.PlaybackSpeed);
        ApplyOutlineVisibility();
        UpdateThemeButton();
        UpdateButtons();
    }

    // ------------------------------------------------------------------ ciclo de vida

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildServices();
        _tray = new TrayIcon(
            open: () => _ = BringToFrontAsync(null),
            readClipboard: () => _ = ReadClipboardAloudAsync(),
            exit: ExitApp);
        // Al cerrar sesión o apagar, Windows cierra la ventana: no hay que esconderla en la bandeja.
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        _hotkey = GlobalHotkey.TryRegister(this, ModifierKeys.Control | ModifierKeys.Alt, Key.L, () => _ = ReadClipboardAloudAsync());

        try
        {
            _view = new DocumentView(Browser, _paths);
            // Los eventos llegan dentro de callbacks de WebView2: se difieren para no abrir diálogos ahí.
            _view.PlayFromRequested += anchor => Dispatcher.InvokeAsync(() => OnPlayFromRequested(anchor));
            _view.CommandRequested += command => Dispatcher.InvokeAsync(() => ExecuteCommand(command));
            _view.LinkActivated += uri => Dispatcher.InvokeAsync(() => OnLinkActivated(uri));
            _view.FileDropped += path => Dispatcher.InvokeAsync(() => _ = OpenFilesAsync([path]));
            _view.ScrollChanged += y => Dispatcher.InvokeAsync(() =>
            {
                // Mientras se carga otra pestaña, la página que sigue a la vista es la anterior.
                if (_active is not null && _shownActivation == _activation)
                {
                    _active.State.ScrollY = y;
                }
            });
            await _view.InitializeAsync();
            _view.SetColorScheme(_settings.Theme);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            _view = null;
            Browser.Visibility = Visibility.Collapsed;
            BrowserError.Visibility = Visibility.Visible;
            BrowserError.Text = "No se pudo iniciar el visor (WebView2). Instala o repara \"Microsoft Edge WebView2 Runtime\" " +
                                $"y vuelve a abrir la app.\n\n{ex.Message}";
            return;
        }

        await RestoreTabsAsync();

        if (_services is { Engine.IsConfigured: false })
        {
            ShowStatus("Para empezar, configura Azure o Piper en ⚙ Configuración.");
        }
        else if (_hotkey is null)
        {
            ShowStatus("Ctrl+Alt+L ya lo usa otra app: el atajo no está activo.");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveSession();
        if (!_exiting)
        {
            // Cerrar la ventana deja la app en la bandeja: el atajo sigue activo y, si estaba leyendo, sigue.
            e.Cancel = true;
            Hide();
            if (IsReading) // solo si está sonando: en pausa o detenido no hace falta
            {
                ShowMiniPlayer();
            }

            if (!_settings.TrayHintShown)
            {
                _tray?.ShowHint("Lector MD sigue en la bandeja. Ctrl+Alt+L lee lo que copiaste.");
                _settings.TrayHintShown = true;
                _settingsStore.Save(_settings);
            }

            return;
        }

        _hotkey?.Dispose();
        _tray?.Dispose();
        _player.Dispose();
        _services?.Dispose();
    }

    /// <summary>Salir de verdad (menú de la bandeja o cierre de sesión de Windows).</summary>
    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void RebuildServices()
    {
        _player.Stop();
        if (_services is not null)
        {
            _services.Engine.StatusChanged -= OnEngineStatusChangedBackground;
            _services.Usage.Changed -= OnUsageChangedBackground;
            _services.Dispose();
        }

        _services = EngineServices.Create(_settings, _paths);
        _services.Engine.StatusChanged += OnEngineStatusChangedBackground;
        _services.Usage.Changed += OnUsageChangedBackground;
        _player.SetEngine(_services.Engine);
        _player.PlaybackSpeed = _settings.PlaybackSpeed;
        _lastFallbackReason = null;

        // Recorte de la caché en segundo plano (no bloquea el arranque).
        AudioCacheTrim(_services);

        ShowEngine(_services.Engine.Status.ActiveEngine, _services.Engine.Status.UsingFallback, _services.Engine.Status.Reason);
        Player.SetEngineChoice(_settings.Engine, _settings.HasAzure, _settings.HasGoogle, _settings.HasPiper);
        UpdateUsage();
    }

    /// <summary>Otro motor elegido en el panel del estado: se reconstruye la cadena y se sigue en el mismo párrafo.</summary>
    private void OnEngineChoiceRequested(EngineChoice choice)
    {
        if (choice == _settings.Engine)
        {
            return;
        }

        int resume = _player.CurrentSegmentId;
        _settings.Engine = choice;
        _settingsStore.Save(_settings);
        RebuildServices();
        if (_active is { State.IsEmpty: false })
        {
            _ = ActivateAsync(_active, force: true, resumeSegmentId: resume >= 0 ? resume : null);
        }

        ShowStatus(choice == EngineChoice.Auto
            ? "Motor automático: Azure, y si no está disponible, Google y luego Piper."
            : $"Leyendo solo con {choice} (sin respaldo). Vuelve a Automático para la cadena completa.");
    }

    // Estos eventos llegan desde hilos del motor.
    private void OnEngineStatusChangedBackground(object? sender, EngineStatus status) =>
        Dispatcher.InvokeAsync(() => OnEngineStatusChanged(status));

    private void OnUsageChangedBackground(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateUsage);

    private static void AudioCacheTrim(EngineServices services) =>
        _ = Task.Run(() =>
        {
            try
            {
                services.Cache.Trim(EngineServices.MaxCacheBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        });

    // ------------------------------------------------------------------ pestañas

    private async Task RestoreTabsAsync()
    {
        RestoredTabs restored = TabSession.Restore(_settings, File.Exists);
        foreach (OpenTab tab in restored.Tabs)
        {
            _tabs.Add(new DocumentTab(tab));
        }

        DeleteOrphanPastes();

        if (_fileFromCommandLine is not null)
        {
            await OpenFilesAsync([_fileFromCommandLine]);
        }
        else if (restored.ActiveIndex >= 0)
        {
            await ActivateAsync(_tabs[restored.ActiveIndex]);
        }
        else
        {
            await ShowNoTabsAsync();
        }

        if (restored.Missing.Count > 0)
        {
            ShowStatus(restored.Missing.Count == 1
                ? $"No se reabrió \"{Path.GetFileName(restored.Missing[0])}\": el archivo ya no existe."
                : $"No se reabrieron {restored.Missing.Count} pestañas: sus archivos ya no existen.");
        }
    }

    /// <summary>Abre cada archivo en su pestaña (o va a la que ya lo tiene) y deja activa la del último.</summary>
    private async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        DocumentTab? last = null;
        foreach (string raw in paths)
        {
            string path = Path.GetFullPath(raw);
            DocumentTab? tab = _tabs.FirstOrDefault(t => t.IsFile(path));
            if (tab is null)
            {
                // Una pestaña vacía se aprovecha, como en un navegador.
                if (_active is { State.IsEmpty: true } && last is null)
                {
                    tab = _active;
                    tab.State.Path = path;
                    tab.NotifyChanged();
                }
                else
                {
                    tab = AddTab(new OpenTab { Path = path }, after: last ?? _active);
                }
            }

            last = tab;
        }

        if (last is not null)
        {
            await ActivateAsync(last, force: true);
        }
    }

    private async Task OpenPastedTextAsync(string markdown)
    {
        string file = Path.Combine(_paths.PastedDirectory,
            $"pegado-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.md");
        try
        {
            _paths.EnsureCreated();
            await File.WriteAllTextAsync(file, markdown, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowStatus($"No se pudo guardar el texto pegado: {ex.Message}");
            return;
        }

        DocumentTab tab = _active is { State.IsEmpty: true } ? _active : AddTab(new OpenTab(), after: _active);
        tab.State.Path = file;
        tab.State.IsPasted = true;
        await ActivateAsync(tab, force: true);
    }

    /// <summary>Agrega la pestaña justo después de <paramref name="after"/>, o al final.</summary>
    private DocumentTab AddTab(OpenTab state, DocumentTab? after)
    {
        var tab = new DocumentTab(state);
        int index = after is null ? _tabs.Count : _tabs.IndexOf(after) + 1;
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        return tab;
    }

    private async Task NewTabAsync() => await ActivateAsync(AddTab(new OpenTab(), after: _active));

    /// <param name="resumeSegmentId">Párrafo donde seguir; si es null se usa el que recuerda la pestaña.</param>
    /// <param name="force">Vuelve a cargar el documento aunque la pestaña ya sea la activa.</param>
    private async Task ActivateAsync(DocumentTab tab, bool force = false, int? resumeSegmentId = null)
    {
        if (_services is null || _view is null || (tab == _active && !force && resumeSegmentId is null))
        {
            return;
        }

        int activation = ++_activation;
        RememberPosition();
        _player.Unload(); // cambiar de pestaña pausa la lectura; cada una recuerda su párrafo
        _active = tab;
        SelectTab(tab);
        double scrollY = tab.State.ScrollY;

        if (tab.State.IsEmpty)
        {
            _document = null;
            _chunks = [];
            RebuildTimeline();
            ShowOutline(null);
            DocumentTitle.Text = "";
            Title = "Nueva pestaña — Lector MD";
            await _view.ShowEmptyTabAsync();
            ShowStatus("Pega (Ctrl+V) un texto Markdown o la ruta de un archivo.");
            UpdateButtons();
            SaveSession();
            return;
        }

        SpeechDocument document;
        IReadOnlyList<SpeechChunk> chunks;
        try
        {
            string markdown = await File.ReadAllTextAsync(tab.State.Path);
            if (activation != _activation)
            {
                return;
            }

            // Un texto pegado no tiene carpeta propia: sin ruta, sus enlaces relativos no apuntan a "pegados".
            document = _services.Parse(markdown, tab.State.IsPasted ? null : tab.State.Path);
            chunks = _services.Plan(document);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, $"No se pudo abrir \"{tab.Name}\":\n{ex.Message}", "Lector MD", MessageBoxButton.OK, MessageBoxImage.Warning);
            await CloseTabAsync(tab);
            return;
        }

        int? resume = resumeSegmentId ?? (tab.State.ResumeSegmentId >= 0 ? tab.State.ResumeSegmentId : null);
        _player.Load(document, chunks, resume);
        _document = document;
        _chunks = chunks;
        RebuildTimeline();
        if (tab.State.IsPasted)
        {
            tab.State.Title = document.Segments.FirstOrDefault(s => s.IsHeading)?.PlainText is { Length: > 0 } heading ? heading : "Pegado";
            tab.NotifyChanged();
        }

        await _view.ShowDocumentAsync(document);
        if (activation != _activation)
        {
            return;
        }

        _shownActivation = activation;

        string? resumeAnchor = resume is { } id && id < document.Segments.Count ? document.Segments[id].AnchorId : null;
        if (resumeAnchor is not null)
        {
            await _view.HighlightAsync(resumeAnchor, follow: false);
        }

        if (scrollY > 0)
        {
            await _view.ScrollToYAsync(scrollY);
        }
        else if (resumeAnchor is not null)
        {
            await _view.ScrollToAsync(resumeAnchor);
        }

        // Mientras se restauraba la vista pudo activarse otra pestaña: no pisar su reproductor ni su índice.
        if (activation != _activation || _timeline is not { } timeline)
        {
            return;
        }

        ShowOutline(document);
        if (resume is { } current)
        {
            MarkOutline(current);
            ShowPosition(current);
        }
        else
        {
            Player.SetPosition(document.Title, document.Segments.Count == 0
                ? "El documento no tiene texto para leer."
                : $"{(timeline.Chapters.Any(c => c.IsEstimated) ? "≈ " : "")}{PlayerBar.Format(timeline.Total)} de lectura · doble clic en un párrafo para empezar ahí");
        }

        DocumentTitle.Text = document.Title;
        Title = $"{document.Title} — Lector MD";
        UpdateButtons();
        SaveSession();
    }

    private async Task CloseTabAsync(DocumentTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        bool wasActive = tab == _active;
        if (wasActive)
        {
            _player.Unload();
            _active = null;
            _document = null;
        }

        _syncingTabs = true;
        _tabs.RemoveAt(index);
        _syncingTabs = false;

        if (tab.State.IsPasted)
        {
            TryDelete(tab.State.Path);
        }

        if (!wasActive)
        {
            SaveSession();
        }
        else if (_tabs.Count > 0)
        {
            await ActivateAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else
        {
            await ShowNoTabsAsync();
        }
    }

    private async Task ShowNoTabsAsync()
    {
        _activation++;
        _player.Unload();
        _active = null;
        _document = null;
        _chunks = [];
        RebuildTimeline();
        ShowOutline(null);
        DocumentTitle.Text = "";
        Title = "Lector MD";
        if (_view is not null)
        {
            await _view.ShowWelcomeAsync();
        }

        UpdateButtons();
        SaveSession();
    }

    /// <summary>
    /// Otra instancia nos pasó un archivo (abierto desde el Explorador) o pidió que nos mostremos:
    /// aparece la ventana y el archivo se abre en su pestaña.
    /// </summary>
    public async Task BringToFrontAsync(string? file)
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (file is not null && File.Exists(file) && DocumentView.IsOpenableFile(file))
        {
            await OpenFilesAsync([file]);
        }
    }

    private void SwitchTab(int delta)
    {
        if (_tabs.Count < 2)
        {
            return;
        }

        int index = _active is null ? 0 : _tabs.IndexOf(_active);
        _ = ActivateAsync(_tabs[(index + delta + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>Guarda en la pestaña activa el párrafo donde va la lectura, para seguir ahí al volver.</summary>
    private void RememberPosition()
    {
        if (_active is not null && _player.HasDocument)
        {
            _active.State.ResumeSegmentId = _player.CurrentSegmentId;
        }
    }

    private void SaveSession()
    {
        RememberPosition();
        _settings.OpenTabs = _tabs.Select(t => t.State.Clone()).ToList();
        _settings.ActiveTab = _active is null ? -1 : _tabs.IndexOf(_active);
        _settings.LastFile = null; // ya migrado a pestañas: si no, volvería a abrirse al cerrar todas
        _settingsStore.Save(_settings);
    }

    private void SelectTab(DocumentTab tab)
    {
        _syncingTabs = true;
        TabList.SelectedItem = tab;
        TabList.ScrollIntoView(tab);
        _syncingTabs = false;
    }

    private void DeleteOrphanPastes()
    {
        try
        {
            if (!Directory.Exists(_paths.PastedDirectory))
            {
                return;
            }

            var inUse = _tabs.Where(t => t.State.IsPasted).Select(t => Path.GetFullPath(t.State.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(_paths.PastedDirectory, "*.md"))
            {
                if (!inUse.Contains(Path.GetFullPath(file)))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingTabs && TabList.SelectedItem is DocumentTab tab && tab != _active)
        {
            _ = ActivateAsync(tab);
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is ListBoxItem { DataContext: DocumentTab tab })
        {
            e.Handled = true;
            _ = CloseTabAsync(tab);
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTab tab })
        {
            _ = CloseTabAsync(tab);
        }
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => _ = NewTabAsync();

    // La barra de pestañas no muestra barra de desplazamiento: con muchas pestañas se recorren con la rueda.
    private void OnTabListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(TabList) is not { } scroller)
        {
            return;
        }

        if (e.Delta > 0)
        {
            scroller.LineLeft();
        }
        else
        {
            scroller.LineRight();
        }

        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            T? found = child as T ?? FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ índice

    private void ShowOutline(SpeechDocument? document)
    {
        _outline = document is null ? [] : DocumentOutline.Build(document);
        OutlineEmpty.Text = document is null ? "Abre un documento para ver sus títulos." : "Este documento no tiene títulos.";
        OutlineEmpty.Visibility = _outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.SelectedIndex = -1; // otro documento: la sección marcada del anterior no aplica
        RefreshOutlineTimes();
    }

    /// <summary>Pone a cada título la duración de su capítulo (≈ si todavía es estimada).</summary>
    private void RefreshOutlineTimes()
    {
        int selected = OutlineList.SelectedIndex;
        OutlineList.ItemsSource = _outline.Select(entry =>
        {
            TimelineChapter? chapter = _timeline?.Chapters.FirstOrDefault(c => c.AnchorId == entry.AnchorId);
            string time = chapter is null ? "" : (chapter.IsEstimated ? "≈" : "") + PlayerBar.Format(chapter.Duration);
            return new OutlineItem(entry, time);
        }).ToList();
        OutlineList.SelectedIndex = selected < _outline.Count ? selected : -1;
    }

    private void MarkOutline(int segmentId)
    {
        int index = DocumentOutline.IndexOf(_outline, segmentId);
        OutlineList.SelectedIndex = index;
        if (index >= 0)
        {
            OutlineList.ScrollIntoView(OutlineList.Items[index]);
        }
    }

    private void ApplyOutlineVisibility()
    {
        OutlinePanel.Visibility = _settings.ShowOutline ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.ShowOutline)
        {
            OutlineToggle.SetResourceReference(BackgroundProperty, "LectorSurface2Brush");
        }
        else
        {
            OutlineToggle.Background = Brushes.Transparent;
        }
    }

    private void ToggleOutline()
    {
        _settings.ShowOutline = !_settings.ShowOutline;
        ApplyOutlineVisibility();
        _settingsStore.Save(_settings);
    }

    private void OnOutlineToggleClick(object sender, RoutedEventArgs e) => ToggleOutline();

    // Con WindowChrome, una ventana maximizada se sale unos píxeles de la pantalla: se compensa.
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        RootPanel.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        // Minimizada mientras lee: aparece el mini reproductor; al volver, se va.
        if (WindowState == WindowState.Minimized)
        {
            if (IsReading) // solo si está sonando: en pausa o detenido no hace falta
            {
                ShowMiniPlayer();
            }
        }
        else
        {
            _mini?.Close();
        }
    }

    // ------------------------------------------------------------------ tema

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeService.Toggled();
        ThemeService.Apply(_settings.Theme);
        _view?.SetColorScheme(_settings.Theme);
        UpdateThemeButton();
        _settingsStore.Save(_settings);
    }

    /// <summary>Un solo botón: muestra el sol en oscuro (pasa a claro) y la luna en claro (pasa a oscuro).</summary>
    private void UpdateThemeButton()
    {
        bool dark = ThemeService.IsDark;
        ThemeIcon.Text = dark ? "" : "";
        string label = dark ? "Cambiar a tema claro" : "Cambiar a tema oscuro";
        ThemeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(ThemeButton, label);
    }

    private void OnOutlineItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: OutlineItem item } && _view is not null)
        {
            _ = _view.ScrollToHeadingAsync(item.Entry.AnchorId);
        }
    }

    private void OnOutlineItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: OutlineItem item })
        {
            e.Handled = true;
            OnPlayFromRequested(item.Entry.AnchorId);
        }
    }

    private void OnOutlineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OutlineList.SelectedItem is OutlineItem item)
        {
            e.Handled = true;
            OnPlayFromRequested(item.Entry.AnchorId);
        }
    }

    // ------------------------------------------------------------------ abrir y pegar

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir documento Markdown",
            Filter = "Markdown (*.md;*.markdown;*.mdx)|*.md;*.markdown;*.mdx|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
            Multiselect = true,
        };
        if (_active is { State.IsEmpty: false, State.IsPasted: false } && Path.GetDirectoryName(_active.State.Path) is { } folder
            && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFilesAsync(dialog.FileNames);
        }
    }

    /// <summary>Ctrl+V: rutas de archivos (texto o archivos copiados en el Explorador) o un texto Markdown.</summary>
    /// <summary>
    /// Ctrl+Alt+L o "Leer portapapeles" de la bandeja: abre lo copiado (rutas .md o texto Markdown) en su pestaña
    /// y empieza a leer, aunque la ventana esté oculta. El texto queda como pestaña pegada, para retomarlo y
    /// para que su audio quede en caché.
    /// </summary>
    private async Task ReadClipboardAloudAsync()
    {
        if (!await PasteAsync() || _document is null)
        {
            return;
        }

        if (_services is { Engine.IsConfigured: false })
        {
            ShowStatus("Para leer, configura Azure o Piper en ⚙ Configuración.");
            return;
        }

        // Al activar la pestaña la lectura quedó detenida: esto empieza desde su párrafo guardado o desde el inicio.
        if (_player.State == PlaybackState.Stopped)
        {
            _player.TogglePlayPause();
        }

        // Si la ventana no está al frente (oculta en la bandeja, minimizada o detrás de la app desde la que se copió),
        // el mini reproductor da los controles sin tener que buscarla. Al activarla, el mini se cierra.
        if (!IsVisible || WindowState == WindowState.Minimized || !IsActive)
        {
            ShowMiniPlayer();
        }
    }

    /// <summary>Muestra (o actualiza) el reproductor flotante con el estado actual.</summary>
    private void ShowMiniPlayer()
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow();
            _mini.PlayPauseClicked += TogglePlay;
            _mini.StopClicked += () => _player.Stop();
            _mini.PreviousClicked += () => _player.PreviousSection();
            _mini.NextClicked += () => _player.NextSection();
            _mini.SpeedChanged += OnSpeedChanged;
            _mini.OpenMainClicked += () => _ = BringToFrontAsync(null);
            _mini.SkipRequested += SkipBy;
            _mini.Closed += (_, _) => _mini = null;
        }

        _mini.SetPosition(Player.CurrentSection, Player.CurrentDetail);
        _mini.SetSpeed(_settings.PlaybackSpeed);
        _mini.SetState(_player.State);
        if (_timeline is not null)
        {
            _mini.SetProgress(_timeline.StartOf(Math.Max(0, _player.CurrentSegmentId)), _timeline.Total);
        }

        _mini.Show();
    }

    /// <returns>True si abrió algo (archivos o el texto pegado).</returns>
    private async Task<bool> PasteAsync()
    {
        List<string> files;
        string? text;
        try
        {
            files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : [];
            text = files.Count == 0 && Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException ex)
        {
            ShowStatus($"No se pudo leer el portapapeles: {ex.Message}");
            return false;
        }

        IReadOnlyList<string> paths = files.Count > 0 ? files : PastedPaths.Parse(text);
        if (paths.Count > 0)
        {
            var openable = paths.Where(p => File.Exists(p) && DocumentView.IsOpenableFile(p)).ToList();
            if (openable.Count == 0)
            {
                ShowStatus(paths.Count == 1
                    ? $"No hay un documento Markdown en \"{paths[0]}\"."
                    : "Ninguna de las rutas pegadas es un documento Markdown que exista.");
                return false;
            }

            await OpenFilesAsync(openable);
            if (openable.Count < paths.Count)
            {
                ShowStatus($"Se abrieron {openable.Count} de {paths.Count}: las demás rutas no son documentos Markdown que existan.");
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus("El portapapeles no tiene texto ni rutas de archivos para pegar.");
            return false;
        }

        await OpenPastedTextAsync(text);
        return true;
    }

    private async Task ReloadAsync()
    {
        if (_active is null || _active.State.IsEmpty)
        {
            return;
        }

        int resume = _player.CurrentSegmentId;
        RebuildServices(); // vuelve a cargar también el diccionario de pronunciación
        await ActivateAsync(_active, force: true, resumeSegmentId: resume >= 0 ? resume : null);
        ShowStatus("Documento recargado.");
    }

    // ------------------------------------------------------------------ comandos

    private void ExecuteCommand(string command)
    {
        switch (command)
        {
            case "toggle": TogglePlay(); break;
            case "stop": _player.Stop(); break;
            case "next": _player.NextSection(); break;
            case "previous": _player.PreviousSection(); break;
            case "open": _ = OpenWithDialogAsync(); break;
            case "settings": OpenSettings(); break;
            case "reload": _ = ReloadAsync(); break;
            case "paste": _ = PasteAsync(); break;
            case "newTab": _ = NewTabAsync(); break;
            case "closeTab" when _active is not null: _ = CloseTabAsync(_active); break;
            case "nextTab": SwitchTab(1); break;
            case "previousTab": SwitchTab(-1); break;
            case "outline": ToggleOutline(); break;
            case "back10": SkipBy(TimeSpan.FromSeconds(-10)); break;
            case "forward10": SkipBy(TimeSpan.FromSeconds(10)); break;
        }
    }

    private void TogglePlay()
    {
        if (_document is null)
        {
            _ = OpenWithDialogAsync();
            return;
        }

        if (_services is { Engine.IsConfigured: false })
        {
            OpenSettings();
            return;
        }

        _player.TogglePlayPause();
    }

    private void OnPlayFromRequested(string anchor)
    {
        if (_document?.FirstForAnchor(anchor) is not { } segment)
        {
            return;
        }

        if (_services is { Engine.IsConfigured: false })
        {
            OpenSettings();
            return;
        }

        _player.PlayFrom(segment.Id);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Solo cuando el foco está en la barra. Dentro del documento los atajos llegan por JavaScript.
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or Microsoft.Web.WebView2.Wpf.WebView2
            || Browser.IsKeyboardFocusWithin)
        {
            return;
        }

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        string? command = e.Key switch
        {
            Key.Space when !ctrl => "toggle",
            Key.Escape => "stop",
            Key.Right when ctrl => "next",
            Key.Left when ctrl => "previous",
            Key.O when ctrl => "open",
            Key.OemComma when ctrl => "settings",
            Key.F5 => "reload",
            Key.V when ctrl => "paste",
            Key.T when ctrl => "newTab",
            Key.W when ctrl => "closeTab",
            Key.F4 when ctrl => "closeTab",
            Key.Tab when ctrl => shift ? "previousTab" : "nextTab",
            Key.B when ctrl => "outline",
            // Flechas solas: −10 s / +10 s (en las listas de pestañas e índice siguen moviendo la selección).
            Key.Left when !ctrl && e.OriginalSource is not ListBoxItem => "back10",
            Key.Right when !ctrl && e.OriginalSource is not ListBoxItem => "forward10",
            _ => null,
        };

        if (command is not null)
        {
            e.Handled = true;
            ExecuteCommand(command);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenWithDialogAsync();
    private void OnPasteClick(object sender, RoutedEventArgs e) => _ = PasteAsync();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSpeedChanged(double speed)
    {
        _player.PlaybackSpeed = speed;
        _settings.PlaybackSpeed = speed;
        Player.SetSpeed(speed);
        _mini?.SetSpeed(speed);
        _settingsStore.Save(_settings);
    }

    /// <summary>Clic en un tramo de la línea de tiempo: leer desde ese capítulo (null = desde el principio).</summary>
    private void OnChapterClicked(string? anchor)
    {
        if (_document is null)
        {
            return;
        }

        if (anchor is not null)
        {
            OnPlayFromRequested(anchor);
        }
        else if (_services is { Engine.IsConfigured: false })
        {
            OpenSettings();
        }
        else
        {
            _player.PlayFrom(0);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedFiles(e) is { Count: > 0 } files)
        {
            _ = OpenFilesAsync(files);
        }
    }

    private static List<string> GetDroppedFiles(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => File.Exists(f) && DocumentView.IsOpenableFile(f)).ToList()
            : [];

    private void OnLinkActivated(Uri uri)
    {
        // Enlaces a otros .md de la misma carpeta: se abren aquí, en su propia pestaña.
        if (_view?.ResolveDocumentLink(uri) is { } local)
        {
            if (File.Exists(local) && DocumentView.IsOpenableFile(local))
            {
                _ = OpenFilesAsync([local]);
            }
            else
            {
                ShowStatus($"No se abre \"{Path.GetFileName(local)}\": solo se siguen enlaces a documentos Markdown.");
            }

            return;
        }

        if (uri.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                ShowStatus($"No se pudo abrir el enlace: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------ configuración

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings.Clone(), _paths) { Owner = this };
        if (window.ShowDialog() != true || window.Result is null)
        {
            return;
        }

        int resume = _player.CurrentSegmentId;
        double speed = _settings.PlaybackSpeed; // la velocidad no se edita en Configuración
        _settings = window.Result;
        _settings.PlaybackSpeed = speed;
        _settingsStore.Save(_settings);
        RebuildServices();

        // Las opciones de lectura (código, tablas) cambian los segmentos: se vuelve a analizar.
        if (_active is { State.IsEmpty: false })
        {
            _ = ActivateAsync(_active, force: true, resumeSegmentId: resume >= 0 ? resume : null);
        }
    }

    // ------------------------------------------------------------------ consumo real en Azure


    private async Task RefreshAzureUsageAsync()
    {
        if (_services is null || !_settings.HasAzure)
        {
            return;
        }

        Player.SetRefreshing(true);
        ShowStatus(File.Exists(_paths.AzureAccountFile)
            ? "Consultando el consumo en Azure…"
            : "Consultando el consumo en Azure… Inicia sesión en el navegador que se abrió.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            AzureUsageClient client = _azureAccount.CreateClient();
            if (string.IsNullOrWhiteSpace(_settings.AzureResourceId))
            {
                SpeechResource? resource = await ChooseSpeechResourceAsync(client, timeout.Token);
                if (resource is null)
                {
                    return;
                }

                _settings.AzureResourceId = resource.Id;
                _settings.AzureTenantId = resource.TenantId;
                _settingsStore.Save(_settings);
            }

            long characters;
            try
            {
                characters = await client.GetMonthCharactersAsync(_settings.AzureResourceId!, _settings.AzureTenantId, timeout.Token);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // El recurso se borró o la cuenta perdió acceso: la próxima vez se vuelve a buscar.
                _settings.AzureResourceId = null;
                _settingsStore.Save(_settings);
                ShowStatus($"No pude leer el consumo del recurso guardado ({ex.Message}). Pulsa ⟳ de nuevo para elegirlo otra vez.");
                return;
            }

            _azureReport = (characters, DateTime.Now);
            int local = _services.Usage.Used;
            bool raised = _services.Usage.SyncFromService(characters);
            UpdateUsage();
            string reported = characters.ToString("N0", Mexico);
            // El contador solo sube: si Azure reporta menos, se avisa por qué la cifra no cambió.
            ShowStatus(raised
                ? $"Azure reporta {reported} caracteres este mes: el contador subió de {local.ToString("N0", Mexico)} a {reported}."
                : $"Azure reporta {reported} caracteres este mes. Tu estimado local ({local.ToString("N0", Mexico)}) es mayor y se mantiene " +
                  "para no pasarte de la cuota (Azure tarda 1–3 min en reflejar lo último).");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("La consulta a Azure tardó demasiado. Vuelve a intentarlo.");
        }
        catch (AuthenticationFailedException ex)
        {
            ErrorLog.Write(ex);
            ShowStatus("No se pudo iniciar sesión en Azure. Si tu cuenta es personal, escribe el Id de directorio en ⚙ Configuración.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            ErrorLog.Write(ex);
            ShowStatus($"No se pudo consultar Azure: {ex.Message}");
        }
        finally
        {
            Player.SetRefreshing(false);
        }
    }

    private async Task<SpeechResource?> ChooseSpeechResourceAsync(AzureUsageClient client, CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeechResource> all = await client.FindSpeechResourcesAsync(_settings.AzureTenantId, cancellationToken);
        if (all.Count == 0)
        {
            ShowStatus("No encontré recursos de Speech en tu cuenta de Azure. Si está en otro directorio, escribe su Id en ⚙ Configuración.");
            return null;
        }

        var inRegion = all.Where(r => r.IsInRegion(_settings.AzureRegion)).ToList();
        IReadOnlyList<SpeechResource> candidates = inRegion.Count > 0 ? inRegion : all;
        return candidates.Count == 1 ? candidates[0] : ResourcePicker.Show(this, candidates);
    }

    // ------------------------------------------------------------------ eventos del reproductor

    private void OnSegmentChanged(SpeechSegment segment)
    {
        if (_view is not null)
        {
            _ = _view.HighlightAsync(segment.AnchorId);
        }

        MarkOutline(segment.Id);
        ShowPosition(segment.Id);
    }

    /// <summary>"§ Sección" y "Párrafo N de M · capítulo X de Y" en el reproductor.</summary>
    private void ShowPosition(int segmentId)
    {
        if (_document is null || _timeline is null || segmentId < 0 || segmentId >= _document.Segments.Count)
        {
            return;
        }

        string section = _document.SectionOf(segmentId)?.PlainText is { Length: > 0 } title ? $"§ {title}" : _document.Title;
        int chapter = _timeline.ChapterIndexOf(segmentId);
        string detail = $"Párrafo {segmentId + 1} de {_document.Segments.Count} · capítulo {chapter + 1} de {_timeline.Chapters.Count}";
        Player.SetPosition(section, detail);
        Player.SetProgress(_timeline.StartOf(segmentId), chapter);
        _mini?.SetPosition(section, detail);
        _mini?.SetProgress(_timeline.StartOf(segmentId), _timeline.Total);
    }

    private void OnProgressChanged(int segmentId, TimeSpan intoSegment)
    {
        if (_timeline is not null)
        {
            TimeSpan elapsed = _timeline.ElapsedAt(segmentId, intoSegment);
            _lastElapsed = elapsed;
            Player.SetProgress(elapsed, _timeline.ChapterIndexOf(segmentId));
            _mini?.SetProgress(elapsed, _timeline.Total);
        }
    }

    /// <summary>
    /// Retrocede o adelanta (−10 s / +10 s). Dentro del bloque que suena mueve la posición sin cortar; si el
    /// destino está en otro bloque, empieza a leer desde el párrafo y punto que da la línea de tiempo.
    /// </summary>
    private void SkipBy(TimeSpan delta)
    {
        if (_document is null || _timeline is null || _services is { Engine.IsConfigured: false })
        {
            return;
        }

        if (_player.State != PlaybackState.Stopped && _player.TrySeekWithinCurrent(delta))
        {
            return;
        }

        TimeSpan from = _player.State == PlaybackState.Stopped
            ? _timeline.StartOf(Math.Max(0, _player.CurrentSegmentId))
            : _lastElapsed;
        TimeSpan target = from + delta;
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > _timeline.Total ? _timeline.Total : target;
        (int segmentId, TimeSpan into) = _timeline.PositionAt(target);
        _player.PlayFrom(segmentId, into);
    }

    /// <summary>Duraciones reales de lo que está en caché y estimadas del resto.</summary>
    /// <param name="keepProgress">True al corregir duraciones del documento que suena (ver PlayerBar.SetTimeline).</param>
    private void RebuildTimeline(bool keepProgress = false)
    {
        _timeline = _document is null || _services is null
            ? null
            : ReadingTimeline.Build(_document, _chunks, chunk => _services.Engine.TryGetCached(chunk));
        Player.SetTimeline(_timeline, keepProgress);
        RefreshOutlineTimes();
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        if (state == PlaybackState.Playing)
        {
            Player.ClearNotice(); // "Preparando el audio…" no debe quedarse mientras ya suena
        }

        if (state == PlaybackState.Loading)
        {
            ShowStatus("Preparando el audio…");
        }

        UpdateButtons();
        _mini?.SetState(state);
    }

    private void OnChunkStarted(AudioChunk audio)
    {
        if (_services is null)
        {
            return;
        }

        EngineStatus status = _services.Engine.Status;
        bool isPiper = audio.EngineName.StartsWith("Piper", StringComparison.Ordinal);
        bool fallback = isPiper && status.UsingFallback;
        string source = audio.FromCache ? "Desde caché · sin gastar cuota"
            : isPiper ? "Generado sin conexión"
            : "Generado con Azure";
        ShowEngine(audio.EngineName, fallback, fallback ? status.Reason : null, source);
        UpdateUsage();

        // Un bloque recién generado ya tiene su duración real: se corrige la línea de tiempo.
        if (!audio.FromCache)
        {
            RebuildTimeline(keepProgress: true);
        }
    }

    private void OnEngineStatusChanged(EngineStatus status)
    {
        if (status.UsingFallback && status.Reason is { } reason && reason != _lastFallbackReason)
        {
            _lastFallbackReason = reason;
            ShowStatus($"Azure no disponible ({reason}). Sigo con Piper.");
        }
        else if (!status.UsingFallback)
        {
            _lastFallbackReason = null;
        }

        ShowEngine(status.ActiveEngine, status.UsingFallback, status.Reason);
    }

    private void OnDocumentFinished()
    {
        if (_view is not null)
        {
            _ = _view.HighlightAsync(null);
        }

        if (_timeline is not null)
        {
            Player.SetProgress(_timeline.Total, _timeline.Chapters.Count - 1);
        }

        ShowStatus("Fin del documento.");
    }

    private void OnPlaybackFailed(Exception ex)
    {
        ErrorLog.Write(ex);
        string message = ex is TtsException tts ? tts.Message : $"No se pudo leer: {ex.Message}";
        ShowStatus(message);

        if (ex is TtsException { Kind: TtsFailureKind.NotConfigured })
        {
            MessageBoxResult answer = MessageBox.Show(this, message + "\n\n¿Abrir la configuración?", "Lector MD",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                OpenSettings();
            }
        }
    }

    // ------------------------------------------------------------------ presentación

    private void ShowStatus(string text)
    {
        Player.ShowNotice(text);
        _mini?.ShowNotice(text);
    }

    /// <param name="source">De dónde salió el último audio ("Desde caché · sin gastar cuota", …).</param>
    private void ShowEngine(string name, bool fallback, string? reason, string? source = null)
    {
        bool none = _services is null || !_services.Engine.IsConfigured;
        string detail = none ? "Configúralo en ⚙"
            : fallback ? $"Respaldo: {reason}"
            : source ?? (name.StartsWith("Piper", StringComparison.Ordinal) ? "Voz local, sin conexión" : "Lista para leer");
        Player.SetEngine(none ? "Sin motor de voz" : FriendlyEngineName(name), detail,
            none ? StatusBrushes.Error : fallback ? StatusBrushes.Fallback : StatusBrushes.Ok,
            reason);
    }

    /// <summary>"Azure · es-MX-DaliaNeural" → "Dalia · Azure"; "Piper · es_MX-claude-high" → "Claude · Piper".</summary>
    private static string FriendlyEngineName(string name)
    {
        string[] parts = name.Split(" · ", 2);
        if (parts.Length != 2)
        {
            return name;
        }

        string voice = Regex.Replace(parts[1], @"^[a-z]{2}[-_][A-Z]{2}[-_]", "");
        voice = Regex.Replace(voice, @"(Neural|-(x_)?(low|medium|high))$", "");
        return voice.Length == 0 ? name : $"{char.ToUpperInvariant(voice[0])}{voice[1..]} · {parts[0]}";
    }

    private void UpdateUsage()
    {
        if (_services is not null && _settings.HasGoogle)
        {
            int used = _services.GoogleUsage.Used;
            int limit = Math.Max(1, _services.GoogleUsage.Limit);
            Player.SetGoogleUsage($"Google: {used.ToString("N0", Mexico)} de {limit.ToString("N0", Mexico)} caracteres " +
                                  $"({((double)used / limit * 100).ToString("0.#", Mexico)} %)");
        }
        else
        {
            Player.SetGoogleUsage(null);
        }

        if (_services is null || !_settings.HasAzure || _settings.OfflineOnly)
        {
            Player.SetUsage(false, 0, 1, DateTime.UtcNow, null);
            return;
        }

        string? report = _azureReport is { } r
            ? $"Azure Monitor: {r.Characters.ToString("N0", Mexico)} a las {r.At.ToString("HH:mm", Mexico)}"
            : null;
        Player.SetUsage(true, _services.Usage.Used, Math.Max(1, _services.Usage.Limit), _services.Usage.ResetsOn, report);
    }

    private void UpdateButtons() => Player.SetState(_player.State, _player.HasDocument);
}

internal static class StatusBrushes
{
    public static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x2E, 0xA0, 0x5B));
    public static readonly SolidColorBrush Fallback = Freeze(Color.FromRgb(0xD9, 0x9A, 0x06));
    public static readonly SolidColorBrush Error = Freeze(Color.FromRgb(0xD1, 0x34, 0x38));

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
