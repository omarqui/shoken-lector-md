using LectorMd.Core.Caching;
using LectorMd.Core.Model;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Settings;
using LectorMd.Core.Ssml;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;

namespace LectorMd.Core;

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

    public static EngineServices Create(AppSettings settings, AppPaths paths)
    {
        paths.EnsureCreated();

        var parser = new MarkdownSpeechParser(new MarkdownSpeechOptions
        {
            CodeBlocks = settings.CodeBlocks,
            Tables = settings.Tables,
            ReadImageAltText = settings.ReadImageAltText,
        });
        var normalizer = new SpeechTextNormalizer(PronunciationDictionary.LoadOrCreate(paths.PronunciationFile));
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

        PiperTtsEngine? piper = !string.IsNullOrWhiteSpace(settings.PiperExePath) && !string.IsNullOrWhiteSpace(settings.PiperModelPath)
            ? new PiperTtsEngine(
                new PiperTtsOptions
                {
                    ExePath = settings.PiperExePath,
                    ModelPath = settings.PiperModelPath,
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

        // Automático: Azure → Google → Piper. Si se eligió un motor para probarlo, se usa solo ese (sin respaldo).
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

        PiperTtsEngine? local = choice is EngineChoice.Auto or EngineChoice.Piper ? piper : null;
        if (local is null)
        {
            piper?.Dispose();
        }

        var engine = new HybridTtsEngine(clouds, local, settings.OfflineOnly || choice == EngineChoice.Piper);
        return new EngineServices(parser, new ChunkingOptions(), normalizer, cache, usage, googleUsage, engine);
    }

    // Una sola conexión HTTP para toda la app (crear un HttpClient por motor agota los sockets).
    private static readonly HttpClient GoogleHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    public SpeechDocument Parse(string markdown, string? sourcePath) => Parser.Parse(markdown, sourcePath);

    public IReadOnlyList<SpeechChunk> Plan(SpeechDocument document) => ChunkPlanner.Plan(document.Segments, Chunking);

    public void Dispose() => Engine.Dispose();
}
