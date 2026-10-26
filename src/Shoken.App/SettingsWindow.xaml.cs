using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Shoken.App.Services;
using Shoken.Core;
using Shoken.Core.Caching;
using Shoken.Core.Notes;
using Shoken.Core.Parsing;
using Shoken.Core.Planning;
using Shoken.Core.Settings;
using Shoken.Core.Ssml;
using Shoken.Core.Text;
using Shoken.Core.Tts;
using Shoken.Core.Usage;

namespace Shoken.App;

public partial class SettingsWindow : Window
{
    private static readonly string[] MexicanVoices =
    [
        "es-MX-DaliaNeural", "es-MX-JorgeNeural", "es-MX-BeatrizNeural", "es-MX-CandelaNeural",
        "es-MX-CarlotaNeural", "es-MX-CecilioNeural", "es-MX-GerardoNeural", "es-MX-LarissaNeural",
        "es-MX-LibertoNeural", "es-MX-LucianoNeural", "es-MX-MarinaNeural", "es-MX-NuriaNeural",
        "es-MX-PelayoNeural", "es-MX-RenataNeural", "es-MX-YagoNeural",
    ];

    // Neural2 en español latino: soportan SSML y marcas de tiempo (el resaltado funciona igual que con Azure).
    private static readonly string[] GoogleVoices = ["es-US-Neural2-A", "es-US-Neural2-B", "es-US-Neural2-C"];

    private readonly AppSettings _original;
    private readonly AppPaths _paths;
    private readonly MediaPlayer _testPlayer = new();

    public SettingsWindow(AppSettings settings, AppPaths paths)
    {
        InitializeComponent();
        _original = settings;
        _paths = paths;
        Closed += (_, _) => _testPlayer.Close();

        AzureKeyBox.Password = settings.AzureKey;
        AzureRegionBox.Text = settings.AzureRegion;
        foreach (string voice in MexicanVoices)
        {
            AzureVoiceBox.Items.Add(voice);
        }

        AzureVoiceBox.Text = settings.AzureVoice;
        LimitBox.Text = settings.MonthlyCharacterLimit.ToString(CultureInfo.InvariantCulture);
        AzureTenantBox.Text = settings.AzureTenantId ?? "";
        GoogleKeyBox.Password = settings.GoogleApiKey;
        foreach (string voice in GoogleVoices)
        {
            GoogleVoiceBox.Items.Add(voice);
        }

        GoogleVoiceBox.Text = settings.GoogleVoice;
        GoogleLimitBox.Text = settings.GoogleMonthlyCharacterLimit.ToString(CultureInfo.InvariantCulture);
        OfflineOnlyBox.IsChecked = settings.OfflineOnly;
        RateSlider.Value = Math.Clamp(settings.RatePercent, -30, 30);
        UpdateRateLabel();
        CodeModeBox.SelectedIndex = (int)settings.CodeBlocks;
        TableModeBox.SelectedIndex = (int)settings.Tables;
        ImageAltBox.IsChecked = settings.ReadImageAltText;
        HighlightWordBox.IsChecked = settings.HighlightWord;
        TextSizeBox.SelectedIndex = (int)settings.TextSize;
        PaletteBox.SelectedIndex = (int)settings.Palette;
        NotesInstructionBox.Text = settings.NotesInstruction ?? NoteExporter.DefaultInstruction;
        foreach ((string id, string name, string kind) in KokoroTtsEngine.Voices)
        {
            KokoroVoiceBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{name} ({kind})", Tag = id });
        }

        KokoroVoiceBox.SelectedIndex = Math.Max(0, KokoroTtsEngine.Voices.ToList().FindIndex(v => v.Id == settings.KokoroVoice));
        Closed += (_, _) => _kokoroDownload?.Cancel();
        UpdateKokoroStatus();
        _piper = new PiperDownloads(paths.PiperDirectory);
        foreach (PiperVoice voice in PiperDownloads.Voices)
        {
            PiperVoiceBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{voice.Name} ({voice.Kind})", Tag = voice });
        }

        PiperVoiceBox.SelectedIndex = Math.Max(0, PiperDownloads.Voices.ToList().FindIndex(v => v.Id == settings.PiperVoice));
        Closed += (_, _) => _piperDownload?.Cancel();
        UpdatePiperStatus();
        UpdateCacheInfo();
    }

    /// <summary>Vista previa del tema: quien abrió la ventana lo aplica y lo restaura si se cancela.</summary>
    public Action<ThemePalette>? PreviewPalette { get; init; }

    /// <summary>Configuración resultante si se guardó.</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>Se guardó con «Escuchar la demo de entonación»: la ventana principal abre la demo.</summary>
    public bool DemoRequested { get; private set; }

    // Guarda como «Guardar» (para no perder lo cambiado) y pide la demo; si no se guardó, no se abre.
    private void OnDemoClick(object sender, RoutedEventArgs e)
    {
        DemoRequested = true;
        OnSaveClick(sender, e);
        DemoRequested = Result is not null;
    }

    private AppSettings ReadForm()
    {
        AppSettings settings = _original.Clone();
        settings.AzureKey = AzureKeyBox.Password.Trim();
        settings.AzureRegion = AzureRegionBox.Text.Trim().ToLowerInvariant();
        settings.AzureVoice = string.IsNullOrWhiteSpace(AzureVoiceBox.Text) ? "es-MX-DaliaNeural" : AzureVoiceBox.Text.Trim();
        settings.PiperVoice = SelectedPiperVoice.Id;
        settings.OfflineOnly = OfflineOnlyBox.IsChecked == true;
        settings.RatePercent = (int)Math.Round(RateSlider.Value);
        settings.CodeBlocks = (CodeBlockMode)Math.Max(0, CodeModeBox.SelectedIndex);
        settings.Tables = (TableMode)Math.Max(0, TableModeBox.SelectedIndex);
        settings.ReadImageAltText = ImageAltBox.IsChecked == true;
        settings.HighlightWord = HighlightWordBox.IsChecked == true;
        settings.TextSize = (TextSize)Math.Max(0, TextSizeBox.SelectedIndex);
        settings.Palette = (ThemePalette)Math.Max(0, PaletteBox.SelectedIndex);
        // La de la app se guarda como null: así se recibe cuando la app la mejore.
        string instruction = NotesInstructionBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        settings.NotesInstruction = instruction.Length == 0 || instruction == NoteExporter.DefaultInstruction ? null : instruction;
        settings.KokoroVoice = (KokoroVoiceBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "ef_dora";
        settings.AzureTenantId = string.IsNullOrWhiteSpace(AzureTenantBox.Text) ? null : AzureTenantBox.Text.Trim();
        settings.GoogleApiKey = GoogleKeyBox.Password.Trim();
        settings.GoogleVoice = string.IsNullOrWhiteSpace(GoogleVoiceBox.Text) ? "es-US-Neural2-A" : GoogleVoiceBox.Text.Trim();
        if (int.TryParse(GoogleLimitBox.Text.Replace(",", "").Replace(".", "").Replace(" ", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int googleLimit))
        {
            settings.GoogleMonthlyCharacterLimit = Math.Max(0, googleLimit);
        }

        // Otra clave, región o directorio puede ser otro recurso: se vuelve a buscar en la próxima consulta.
        if (settings.AzureKey != _original.AzureKey || settings.AzureRegion != _original.AzureRegion
            || settings.AzureTenantId != _original.AzureTenantId)
        {
            settings.AzureResourceId = null;
        }

        if (int.TryParse(LimitBox.Text.Replace(",", "").Replace(".", "").Replace(" ", ""), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int limit))
        {
            settings.MonthlyCharacterLimit = Math.Max(0, limit);
        }

        return settings;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        // Con el programa descargado, Piper lee solo con la voz elegida (EngineServices.ResolvePiper).
        if (_piper.HasProgram && !_piper.HasVoice(settings.PiperVoice))
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"La voz {SelectedPiperVoice.Name} de Piper no está descargada: Piper no se usará hasta que la descargues."
                + "\n\n¿Guardar de todos modos?", "Configuración",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Result = settings;
        DialogResult = true;
    }

    /// <summary>Vista previa: el tamaño se aplica al instante; quien abrió la ventana lo restaura si se cancela.</summary>
    private void OnTextSizeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && TextSizeBox.SelectedIndex >= 0)
        {
            TextScale.Apply((TextSize)TextSizeBox.SelectedIndex);
        }
    }

    private void OnRestoreNotesInstructionClick(object sender, RoutedEventArgs e) =>
        NotesInstructionBox.Text = NoteExporter.DefaultInstruction;

    private void OnPaletteChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && PaletteBox.SelectedIndex >= 0)
        {
            PreviewPalette?.Invoke((ThemePalette)PaletteBox.SelectedIndex);
        }
    }

    // ------------------------------------------------------------------ pruebas de voz

    private async void OnTestAzureClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        if (!settings.HasAzure)
        {
            ShowResult(AzureTestResult, "Escribe la clave y la región.", ok: false);
            return;
        }

        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile));
        using var engine = new AzureTtsEngine(
            new AzureTtsOptions { Key = settings.AzureKey, Region = settings.AzureRegion, Voice = settings.AzureVoice, RatePercent = settings.RatePercent },
            new SsmlBuilder(normalizer),
            new AudioCache(_paths.CacheDirectory),
            new UsageTracker(_paths.UsageFile, settings.MonthlyCharacterLimit));
        await RunTestAsync(TestAzureButton, AzureTestResult, engine,
            "Hola. Así suena **Azure** leyendo tus documentos en español de México.");
    }

    private async void OnTestGoogleClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        if (!settings.HasGoogle)
        {
            ShowResult(GoogleTestResult, "Escribe la clave de API.", ok: false);
            return;
        }

        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile));
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var engine = new GoogleTtsEngine(
            new GoogleTtsOptions { ApiKey = settings.GoogleApiKey, Voice = settings.GoogleVoice, RatePercent = settings.RatePercent },
            new SsmlBuilder(normalizer),
            new AudioCache(_paths.CacheDirectory),
            new UsageTracker(_paths.GoogleUsageFile, settings.GoogleMonthlyCharacterLimit),
            http);
        await RunTestAsync(TestGoogleButton, GoogleTestResult, engine,
            "Hola. Así suena **Google** leyendo tus documentos cuando Azure no está disponible.");
    }

    private async void OnTestPiperClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        if (EngineServices.ResolvePiper(settings, _paths) is not { } files)
        {
            ShowResult(PiperTestResult, $"Descarga primero Piper con la voz {SelectedPiperVoice.Name}.", ok: false);
            return;
        }

        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile));
        using var engine = new PiperTtsEngine(
            new PiperTtsOptions { ExePath = files.ExePath, ModelPath = files.ModelPath, RatePercent = settings.RatePercent },
            normalizer, new AudioCache(_paths.CacheDirectory), _paths.TempDirectory);
        await RunTestAsync(TestPiperButton, PiperTestResult, engine,
            "Hola. Así suena Piper, la voz sin conexión, cuando Azure no está disponible.");
    }

    private async void OnTestKokoroClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile));
        using var engine = new KokoroTtsEngine(
            new KokoroTtsOptions
            {
                ModelPath = _paths.KokoroModelFile,
                Voice = settings.KokoroVoice,
                VoicesDirectory = EngineServices.KokoroVoicesDirectory,
                RatePercent = settings.RatePercent,
            },
            normalizer, new AudioCache(_paths.CacheDirectory));
        await RunTestAsync(TestKokoroButton, KokoroTestResult, engine,
            "Hola. Así suena Kokoro, la voz natural sin conexión. Tarda unos quince minutos en la calle.");
    }

    // ------------------------------------------------------------------ modelo de Kokoro

    private static readonly HttpClient DownloadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _kokoroDownload;

    private void UpdateKokoroStatus()
    {
        bool installed = File.Exists(_paths.KokoroModelFile);
        KokoroDownloadButton.Content = installed ? "Volver a descargar" : "Descargar modelo (310 MB)";
        KokoroModelStatus.Text = installed ? "✓ Instalado" : "No instalado: Kokoro no se usa hasta descargarlo.";
        TestKokoroButton.IsEnabled = installed;
    }

    private async void OnDownloadKokoroClick(object sender, RoutedEventArgs e)
    {
        _kokoroDownload = new CancellationTokenSource();
        KokoroDownloadButton.IsEnabled = false;
        TestKokoroButton.IsEnabled = false;
        var progress = new Progress<double>(fraction =>
            KokoroModelStatus.Text = $"Descargando… {fraction * 100:0} % de 310 MB");
        try
        {
            await KokoroModelDownloader.DownloadAsync(DownloadHttp, _paths.KokoroModelFile, progress, _kokoroDownload.Token);
            UpdateKokoroStatus();
        }
        catch (OperationCanceledException)
        {
            // Se cerró la ventana: no queda nada a medias.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            UpdateKokoroStatus();
            KokoroModelStatus.Text = $"✗ No se pudo descargar: {ex.Message}";
        }
        finally
        {
            KokoroDownloadButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ descarga de Piper

    private PiperDownloads _piper = null!;
    private CancellationTokenSource? _piperDownload;

    private PiperVoice SelectedPiperVoice =>
        (PiperVoiceBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as PiperVoice ?? PiperDownloads.Voices[0];

    // Como el Explorador y el «310 MB» de Kokoro: en MB de 1024².
    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0} MB";

    private void OnPiperVoiceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded && _piperDownload is null)
        {
            UpdatePiperStatus();
        }
    }

    private void UpdatePiperStatus()
    {
        PiperVoice voice = SelectedPiperVoice;
        long missing = _piper.BytesToDownload(voice, everything: false);
        if (missing == 0)
        {
            PiperDownloadButton.Content = "Volver a descargar";
            PiperDownloadStatus.Text = "✓ Instalado";
        }
        else if (_piper.HasProgram)
        {
            PiperDownloadButton.Content = $"Descargar voz ({Megabytes(missing)})";
            PiperDownloadStatus.Text = $"Falta la voz {voice.Name}.";
        }
        else
        {
            PiperDownloadButton.Content = $"Descargar Piper ({Megabytes(missing)})";
            PiperDownloadStatus.Text = "No instalado: Piper no se usa hasta descargarlo.";
        }

        PiperDownloadStatus.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    private async void OnDownloadPiperClick(object sender, RoutedEventArgs e)
    {
        PiperVoice voice = SelectedPiperVoice;
        bool everything = _piper.BytesToDownload(voice, everything: false) == 0; // «Volver a descargar»
        string size = Megabytes(_piper.BytesToDownload(voice, everything));
        _piperDownload = new CancellationTokenSource();
        PiperDownloadButton.IsEnabled = false;
        PiperVoiceBox.IsEnabled = false;
        TestPiperButton.IsEnabled = false;
        var progress = new Progress<double>(fraction => PiperDownloadStatus.Text = $"Descargando… {fraction * 100:0} % de {size}");
        try
        {
            await _piper.DownloadAsync(DownloadHttp, voice, everything, progress, _piperDownload.Token);
            UpdatePiperStatus();
        }
        catch (OperationCanceledException)
        {
            // Se cerró la ventana: no queda nada a medias.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ErrorLog.Write(ex);
            UpdatePiperStatus();
            ShowResult(PiperDownloadStatus, $"No se pudo descargar: {ex.Message}", ok: false);
        }
        finally
        {
            _piperDownload = null;
            PiperDownloadButton.IsEnabled = true;
            PiperVoiceBox.IsEnabled = true;
            TestPiperButton.IsEnabled = true;
        }
    }

    private async Task RunTestAsync(System.Windows.Controls.Button button, System.Windows.Controls.TextBlock result,
        ITtsEngine engine, string markdown)
    {
        if (!engine.IsConfigured)
        {
            ShowResult(result, "Faltan archivos o datos para este motor.", ok: false);
            return;
        }

        button.IsEnabled = false;
        ShowResult(result, "Generando…", ok: null);
        try
        {
            var document = new MarkdownSpeechParser().Parse(markdown);
            var chunk = new SpeechChunk { Index = 0, Segments = document.Segments };
            AudioChunk audio = await Task.Run(() => engine.SynthesizeAsync(chunk, CancellationToken.None));
            _testPlayer.Open(new Uri(audio.FilePath, UriKind.Absolute));
            _testPlayer.Play();
            ShowResult(result, audio.FromCache ? "Funciona (audio de la caché)." : "Funciona.", ok: true);
            UpdateCacheInfo();
        }
        catch (TtsException ex)
        {
            ShowResult(result, ex.Message, ok: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            ShowResult(result, ex.Message, ok: false);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static void ShowResult(System.Windows.Controls.TextBlock target, string text, bool? ok)
    {
        target.Text = ok switch { true => "✓ " + text, false => "✗ " + text, null => text };
        if (ok is null)
        {
            target.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        }
        else
        {
            target.Foreground = ok.Value ? StatusBrushes.Ok : StatusBrushes.Error;
        }
    }

    // ------------------------------------------------------------------ archivos

    private void OnEditDictionaryClick(object sender, RoutedEventArgs e)
    {
        PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile); // lo crea con los valores iniciales si no existe
        StartProcess("notepad.exe", $"\"{_paths.PronunciationFile}\"");
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        _paths.EnsureCreated();
        StartProcess("explorer.exe", $"\"{_paths.Root}\"");
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(this,
            "Se borrará el audio guardado. Volver a escuchar un documento leído con Azure gastará cuota otra vez.\n\n¿Continuar?",
            "Vaciar caché", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            new AudioCache(_paths.CacheDirectory).Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }

        UpdateCacheInfo();
    }

    private void UpdateCacheInfo()
    {
        long bytes = new AudioCache(_paths.CacheDirectory).SizeBytes();
        CacheInfo.Text = $"Caché de audio: {bytes / (1024.0 * 1024.0):0.0} MB · Datos en {_paths.Root}";
    }

    private void OnRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsInitialized)
        {
            UpdateRateLabel();
        }
    }

    private void UpdateRateLabel()
    {
        int value = (int)Math.Round(RateSlider.Value);
        RateLabel.Text = (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture) + " %";
    }

    private static void StartProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(ex);
        }
    }
}
