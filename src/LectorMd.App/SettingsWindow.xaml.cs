using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LectorMd.App.Services;
using LectorMd.Core.Caching;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Settings;
using LectorMd.Core.Ssml;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;
using Microsoft.Win32;

namespace LectorMd.App;

public partial class SettingsWindow : Window
{
    private static readonly string[] MexicanVoices =
    [
        "es-MX-DaliaNeural", "es-MX-JorgeNeural", "es-MX-BeatrizNeural", "es-MX-CandelaNeural",
        "es-MX-CarlotaNeural", "es-MX-CecilioNeural", "es-MX-GerardoNeural", "es-MX-LarissaNeural",
        "es-MX-LibertoNeural", "es-MX-LucianoNeural", "es-MX-MarinaNeural", "es-MX-NuriaNeural",
        "es-MX-PelayoNeural", "es-MX-RenataNeural", "es-MX-YagoNeural",
    ];

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
        PiperExeBox.Text = settings.PiperExePath;
        PiperModelBox.Text = settings.PiperModelPath;
        OfflineOnlyBox.IsChecked = settings.OfflineOnly;
        RateSlider.Value = Math.Clamp(settings.RatePercent, -30, 30);
        UpdateRateLabel();
        CodeModeBox.SelectedIndex = (int)settings.CodeBlocks;
        TableModeBox.SelectedIndex = (int)settings.Tables;
        ImageAltBox.IsChecked = settings.ReadImageAltText;
        UpdateCacheInfo();
    }

    /// <summary>Configuración resultante si se guardó.</summary>
    public AppSettings? Result { get; private set; }

    private AppSettings ReadForm()
    {
        AppSettings settings = _original.Clone();
        settings.AzureKey = AzureKeyBox.Password.Trim();
        settings.AzureRegion = AzureRegionBox.Text.Trim().ToLowerInvariant();
        settings.AzureVoice = string.IsNullOrWhiteSpace(AzureVoiceBox.Text) ? "es-MX-DaliaNeural" : AzureVoiceBox.Text.Trim();
        settings.PiperExePath = PiperExeBox.Text.Trim().Trim('"');
        settings.PiperModelPath = PiperModelBox.Text.Trim().Trim('"');
        settings.OfflineOnly = OfflineOnlyBox.IsChecked == true;
        settings.RatePercent = (int)Math.Round(RateSlider.Value);
        settings.CodeBlocks = (CodeBlockMode)Math.Max(0, CodeModeBox.SelectedIndex);
        settings.Tables = (TableMode)Math.Max(0, TableModeBox.SelectedIndex);
        settings.ReadImageAltText = ImageAltBox.IsChecked == true;
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
        var problems = new List<string>();
        if (!string.IsNullOrEmpty(settings.PiperExePath) && !File.Exists(settings.PiperExePath))
        {
            problems.Add("No encuentro piper.exe en la ruta indicada.");
        }

        if (!string.IsNullOrEmpty(settings.PiperModelPath))
        {
            if (!File.Exists(settings.PiperModelPath))
            {
                problems.Add("No encuentro el modelo .onnx de Piper.");
            }
            else if (!File.Exists(settings.PiperModelPath + ".json"))
            {
                problems.Add($"Falta {Path.GetFileName(settings.PiperModelPath)}.json junto al modelo.");
            }
        }

        if (problems.Count > 0)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                string.Join("\n", problems) + "\n\n¿Guardar de todos modos?", "Configuración",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        Result = settings;
        DialogResult = true;
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

    private async void OnTestPiperClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = ReadForm();
        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(_paths.PronunciationFile));
        using var engine = new PiperTtsEngine(
            new PiperTtsOptions { ExePath = settings.PiperExePath, ModelPath = settings.PiperModelPath, RatePercent = settings.RatePercent },
            normalizer, new AudioCache(_paths.CacheDirectory), _paths.TempDirectory);
        await RunTestAsync(TestPiperButton, PiperTestResult, engine,
            "Hola. Así suena Piper, la voz sin conexión, cuando Azure no está disponible.");
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

    private void OnBrowsePiperClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ubicar piper.exe", Filter = "piper.exe|piper.exe|Ejecutables (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true)
        {
            PiperExeBox.Text = dialog.FileName;
        }
    }

    private void OnBrowseModelClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ubicar el modelo de voz", Filter = "Modelo Piper (*.onnx)|*.onnx" };
        if (dialog.ShowDialog(this) == true)
        {
            PiperModelBox.Text = dialog.FileName;
        }
    }

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
