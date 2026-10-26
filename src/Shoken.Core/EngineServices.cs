using Shoken.Core.Caching;
using Shoken.Core.Model;
using Shoken.Core.Parsing;
using Shoken.Core.Planning;
using Shoken.Core.Settings;
using Shoken.Core.Ssml;
using Shoken.Core.Text;
using Shoken.Core.Tts;
using Shoken.Core.Usage;

namespace Shoken.Core;

/// <summary>Arma todas las piezas (parser, normalizador, caché, cuota y motores) a partir de la configuración.</summary>
public sealed class EngineServices : IDisposable
{
    /// <summary>Tamaño máximo de la caché de audio antes de recortar lo menos usado.</summary>
    public const long MaxCacheBytes = 1L * 1024 * 1024 * 1024;

    private EngineServices(
        MarkdownSpeechParser parser,
        ChunkingOptions chunking,
        SpeechTextNormalizer normalizer,
        AudioCache cache,
        UsageTracker usage,
        UsageTracker googleUsage,
        HybridTtsEngine engine)
    {
        Parser = parser;
        Chunking = chunking;
        Normalizer = normalizer;
        Cache = cache;
        Usage = usage;
        GoogleUsage = googleUsage;
        Engine = engine;
    }

    public MarkdownSpeechParser Parser { get; }
    public ChunkingOptions Chunking { get; }
    public SpeechTextNormalizer Normalizer { get; }
    public AudioCache Cache { get; }
    /// <summary>Contador de Azure.</summary>
    public UsageTracker Usage { get; }

    /// <summary>Contador de Google (archivo y límite propios).</summary>
    public UsageTracker GoogleUsage { get; }

    public HybridTtsEngine Engine { get; }

    /// <param name="dictionary">Para escuchar una pronunciación antes de guardarla; null = la del archivo del usuario.</param>
    public static EngineServices Create(AppSettings settings, AppPaths paths, PronunciationDictionary? dictionary = null)
    {
        paths.EnsureCreated();

        var parser = new MarkdownSpeechParser(new MarkdownSpeechOptions
        {
            CodeBlocks = settings.CodeBlocks,
            Tables = settings.Tables,
            ReadImageAltText = settings.ReadImageAltText,
        });
        var normalizer = new SpeechTextNormalizer(dictionary ?? PronunciationDictionary.LoadOrCreate(paths.PronunciationFile));
        var cache = new AudioCache(paths.CacheDirectory);
        var usage = new UsageTracker(paths.UsageFile, settings.MonthlyCharacterLimit);

        AzureTtsEngine? azure = settings.HasAzure
            ? new AzureTtsEngine(
                new AzureTtsOptions
                {
                    Key = settings.AzureKey,
                    Region = settings.AzureRegion,
                    Voice = string.IsNullOrWhiteSpace(settings.AzureVoice) ? "es-MX-DaliaNeural" : settings.AzureVoice.Trim(),
                    RatePercent = settings.RatePercent,
                },
                new SsmlBuilder(normalizer), cache, usage)
            : null;

        PiperTtsEngine? piper = ResolvePiper(settings, paths) is { } piperFiles
            ? new PiperTtsEngine(
                new PiperTtsOptions
                {
                    ExePath = piperFiles.ExePath,
                    ModelPath = piperFiles.ModelPath,
                    RatePercent = settings.RatePercent,
                },
                normalizer, cache, paths.TempDirectory)
            : null;

        var googleUsage = new UsageTracker(paths.GoogleUsageFile, settings.GoogleMonthlyCharacterLimit);
        GoogleTtsEngine? google = settings.HasGoogle
            ? new GoogleTtsEngine(
                new GoogleTtsOptions
                {
                    ApiKey = settings.GoogleApiKey,
                    Voice = string.IsNullOrWhiteSpace(settings.GoogleVoice) ? "es-US-Neural2-A" : settings.GoogleVoice.Trim(),
                    RatePercent = settings.RatePercent,
                },
                new SsmlBuilder(normalizer), cache, googleUsage, GoogleHttp)
            : null;

        // Kokoro: el modelo se descarga desde Configuración; las voces las trae la app. Crearlo no carga el modelo.
        var kokoro = new KokoroTtsEngine(
            new KokoroTtsOptions
            {
                ModelPath = paths.KokoroModelFile,
                Voice = string.IsNullOrWhiteSpace(settings.KokoroVoice) ? "ef_dora" : settings.KokoroVoice.Trim(),
                VoicesDirectory = KokoroVoicesDirectory,
                RatePercent = settings.RatePercent,
            },
            normalizer, cache);

        // Automático: Azure → Google → Kokoro → Piper. Si se eligió un motor para probarlo, se usa solo ese (sin respaldo).
        EngineChoice choice = settings.Engine;
        var clouds = new List<CloudEngine>();
        if (azure is not null && choice is EngineChoice.Auto or EngineChoice.Azure)
        {
            clouds.Add(new CloudEngine(azure, usage, "Azure"));
        }

        if (google is not null && choice is EngineChoice.Auto or EngineChoice.Google)
        {
            clouds.Add(new CloudEngine(google, googleUsage, "Google"));
        }

        var locals = new List<ITtsEngine>();
        if (choice is EngineChoice.Auto or EngineChoice.Kokoro)
        {
            locals.Add(kokoro);
        }
        else
        {
            kokoro.Dispose();
        }

        if (piper is not null && choice is EngineChoice.Auto or EngineChoice.Piper)
        {
            locals.Add(piper);
        }
        else
        {
            piper?.Dispose();
        }

        bool offline = settings.OfflineOnly || choice is EngineChoice.Kokoro or EngineChoice.Piper;
        var engine = new HybridTtsEngine(clouds, locals, offline);
        return new EngineServices(parser, new ChunkingOptions(), normalizer, cache, usage, googleUsage, engine);
    }

    /// <summary>Voces de Kokoro en español (.npy), junto al ejecutable de la app.</summary>
    public static string KokoroVoicesDirectory => Path.Combine(AppContext.BaseDirectory, "kokoro");

    /// <summary>
    /// Qué Piper se usa: el descargado desde Configuración, con la voz elegida. Las rutas elegidas a mano (de antes de
    /// la descarga) solo cuentan si no se descargó el programa. Null si falta algo.
    /// </summary>
    public static PiperFiles? ResolvePiper(AppSettings settings, AppPaths paths)
    {
        var downloads = new PiperDownloads(paths.PiperDirectory);
        if (downloads.HasProgram)
        {
            return downloads.HasVoice(settings.PiperVoice)
                ? new PiperFiles(downloads.ExePath, downloads.ModelPath(settings.PiperVoice))
                : null;
        }

        return File.Exists(settings.PiperExePath) && File.Exists(settings.PiperModelPath)
            ? new PiperFiles(settings.PiperExePath, settings.PiperModelPath)
            : null;
    }

    // Una sola conexión HTTP para toda la app (crear un HttpClient por motor agota los sockets).
    private static readonly HttpClient GoogleHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SpeechDocument Parse(string markdown, string? sourcePath) => Parser.Parse(markdown, sourcePath);

    public IReadOnlyList<SpeechChunk> Plan(SpeechDocument document) => ChunkPlanner.Plan(document.Segments, Chunking);

    public void Dispose() => Engine.Dispose();
}
