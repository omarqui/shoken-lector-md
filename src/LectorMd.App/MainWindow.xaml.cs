using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core;
using LectorMd.Core.Model;
using LectorMd.Core.Settings;
using LectorMd.Core.Tts;
using Microsoft.Win32;

namespace LectorMd.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppPaths _paths = AppPaths.Default;
    private readonly SettingsStore _settingsStore;
    private readonly ReadAloudController _player = new();
    private readonly DispatcherTimer _saveSpeedTimer;
    private AppSettings _settings;
    private EngineServices? _services;
    private DocumentView? _view;
    private SpeechDocument? _document;
    private readonly string? _fileFromCommandLine;
    private string? _lastFallbackReason;

    public MainWindow(string? fileToOpen)
    {
        InitializeComponent();
        _fileFromCommandLine = fileToOpen;
        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();

        _player.SegmentChanged += OnSegmentChanged;
        _player.StateChanged += OnPlaybackStateChanged;
        _player.ChunkStarted += OnChunkStarted;
        _player.DocumentFinished += OnDocumentFinished;
        _player.Failed += OnPlaybackFailed;

        _saveSpeedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveSpeedTimer.Tick += (_, _) =>
        {
            _saveSpeedTimer.Stop();
            _settingsStore.Save(_settings);
        };

        SpeedSlider.Value = Math.Clamp(_settings.PlaybackSpeed, SpeedSlider.Minimum, SpeedSlider.Maximum);
        UpdateSpeedLabel();
        UpdateButtons();
    }

    // ------------------------------------------------------------------ ciclo de vida

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RebuildServices();

        try
        {
            _view = new DocumentView(Browser, _paths);
            // Los eventos llegan dentro de callbacks de WebView2: se difieren para no abrir diálogos ahí.
            _view.PlayFromRequested += anchor => Dispatcher.InvokeAsync(() => OnPlayFromRequested(anchor));
            _view.CommandRequested += command => Dispatcher.InvokeAsync(() => ExecuteCommand(command));
            _view.LinkActivated += uri => Dispatcher.InvokeAsync(() => OnLinkActivated(uri));
            _view.FileDropped += path => Dispatcher.InvokeAsync(() => _ = OpenFileAsync(path));
            await _view.InitializeAsync();
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

        string? file = _fileFromCommandLine ?? (File.Exists(_settings.LastFile) ? _settings.LastFile : null);
        if (file is not null)
        {
            await OpenFileAsync(file);
        }
        else
        {
            await _view.ShowWelcomeAsync();
        }

        if (_services is { Engine.IsConfigured: false })
        {
            ShowStatus("Para empezar, configura Azure o Piper en ⚙ Configuración.");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.PlaybackSpeed = SpeedSlider.Value;
        _settingsStore.Save(_settings);
        _player.Dispose();
        _services?.Dispose();
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
        _player.PlaybackSpeed = SpeedSlider.Value;
        _lastFallbackReason = null;

        // Recorte de la caché en segundo plano (no bloquea el arranque).
        AudioCacheTrim(_services);

        ShowEngine(_services.Engine.Status.ActiveEngine, _services.Engine.Status.UsingFallback, _services.Engine.Status.Reason);
        UpdateUsage();
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

    // ------------------------------------------------------------------ documento

    private async Task OpenFileAsync(string path, int? resumeSegmentId = null)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            string markdown = await File.ReadAllTextAsync(path);
            SpeechDocument document = _services.Parse(markdown, path);
            var chunks = _services.Plan(document);

            _player.Load(document, chunks, resumeSegmentId);
            _document = document;
            if (_view is not null)
            {
                await _view.ShowDocumentAsync(document);
                if (resumeSegmentId is { } id && id < document.Segments.Count)
                {
                    await _view.HighlightAsync(document.Segments[id].AnchorId);
                    await _view.ScrollToAsync(document.Segments[id].AnchorId);
                }
            }

            DocumentTitle.Text = document.Title;
            Title = $"{document.Title} — Lector MD";
            _settings.LastFile = Path.GetFullPath(path);
            _settingsStore.Save(_settings);

            double minutes = document.CharacterCount / 15.0 / 60.0; // ~15 caracteres por segundo en español
            ShowStatus(document.Segments.Count == 0
                ? "El documento no tiene texto para leer."
                : $"Listo · ≈ {Math.Max(1, Math.Round(minutes)):0} min de lectura · doble clic en un párrafo para empezar ahí");
            UpdateButtons();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            MessageBox.Show(this, $"No se pudo abrir el archivo:\n{ex.Message}", "Lector MD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Abrir documento Markdown",
            Filter = "Markdown (*.md;*.markdown;*.mdx)|*.md;*.markdown;*.mdx|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*",
        };
        if (_settings.LastFile is { } last && Path.GetDirectoryName(last) is { } folder && Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private async Task ReloadAsync()
    {
        if (_document?.SourcePath is not { } path || !File.Exists(path))
        {
            return;
        }

        int resume = _player.CurrentSegmentId;
        RebuildServices(); // vuelve a cargar también el diccionario de pronunciación
        await OpenFileAsync(path, resume >= 0 ? resume : null);
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
        string? command = e.Key switch
        {
            Key.Space when !ctrl => "toggle",
            Key.Escape => "stop",
            Key.Right when ctrl => "next",
            Key.Left when ctrl => "previous",
            Key.O when ctrl => "open",
            Key.OemComma when ctrl => "settings",
            Key.F5 => "reload",
            _ => null,
        };

        if (command is not null)
        {
            e.Handled = true;
            ExecuteCommand(command);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenWithDialogAsync();
    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void OnStopClick(object sender, RoutedEventArgs e) => _player.Stop();
    private void OnNextClick(object sender, RoutedEventArgs e) => _player.NextSection();
    private void OnPreviousClick(object sender, RoutedEventArgs e) => _player.PreviousSection();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _player.PlaybackSpeed = SpeedSlider.Value;
        _settings.PlaybackSpeed = SpeedSlider.Value;
        UpdateSpeedLabel();
        _saveSpeedTimer.Stop();
        _saveSpeedTimer.Start();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFile(e) is { } file)
        {
            _ = OpenFileAsync(file);
        }
    }

    private static string? TryGetDroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => File.Exists(f) && DocumentView.IsOpenableFile(f))
            : null;

    private void OnLinkActivated(Uri uri)
    {
        // Enlaces a otros .md de la misma carpeta: se abren aquí.
        if (_view?.ResolveDocumentLink(uri) is { } local)
        {
            if (File.Exists(local) && DocumentView.IsOpenableFile(local))
            {
                _ = OpenFileAsync(local);
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
        _settings = window.Result;
        _settings.PlaybackSpeed = SpeedSlider.Value;
        _settingsStore.Save(_settings);
        RebuildServices();

        // Las opciones de lectura (código, tablas) cambian los segmentos: se vuelve a analizar.
        if (_document?.SourcePath is { } path && File.Exists(path))
        {
            _ = OpenFileAsync(path, resume >= 0 ? resume : null);
        }
    }

    // ------------------------------------------------------------------ eventos del reproductor

    private void OnSegmentChanged(SpeechSegment segment)
    {
        if (_view is not null)
        {
            _ = _view.HighlightAsync(segment.AnchorId);
        }

        if (_document?.SectionOf(segment.Id) is { } section)
        {
            ShowStatus($"§ {section.PlainText}");
        }
    }

    private void OnPlaybackStateChanged(PlaybackState state)
    {
        (PlayIcon.Text, PlayLabel.Text) = state switch
        {
            PlaybackState.Playing => ("", "Pausar"),
            PlaybackState.Paused => ("", "Seguir"),
            PlaybackState.Loading => ("", "Cargando"),
            _ => ("", "Leer"),
        };

        if (state == PlaybackState.Loading)
        {
            ShowStatus("Preparando el audio…");
        }

        UpdateButtons();
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
        ShowEngine(audio.EngineName, fallback, fallback ? status.Reason : null);
        UpdateUsage();
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

    private void ShowStatus(string text) => StatusText.Text = text;

    private void ShowEngine(string name, bool fallback, string? reason)
    {
        bool none = _services is null || !_services.Engine.IsConfigured;
        EngineText.Text = none ? "Sin motor de voz" : fallback ? $"{name} (respaldo)" : name;
        EngineText.ToolTip = reason is null ? null : $"Motivo: {reason}";
        EngineDot.Fill = none ? StatusBrushes.Error : fallback ? StatusBrushes.Fallback : StatusBrushes.Ok;
    }

    private void UpdateUsage()
    {
        if (_services is null || !_settings.HasAzure || _settings.OfflineOnly)
        {
            UsagePanel.Visibility = Visibility.Collapsed;
            return;
        }

        UsagePanel.Visibility = Visibility.Visible;
        int used = _services.Usage.Used;
        int limit = Math.Max(1, _services.Usage.Limit);
        UsageText.Text = $"Azure: {used.ToString("N0", Mexico)} / {limit.ToString("N0", Mexico)} caracteres este mes";
        UsageBar.Value = Math.Min(1.0, (double)used / limit);
    }

    private void UpdateSpeedLabel() => SpeedLabel.Text = SpeedSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "×";

    private void UpdateButtons()
    {
        bool hasDocument = _player.HasDocument;
        PlayButton.IsEnabled = true;
        StopButton.IsEnabled = _player.State != PlaybackState.Stopped;
        NextButton.IsEnabled = hasDocument;
        PreviousButton.IsEnabled = hasDocument;
    }
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
