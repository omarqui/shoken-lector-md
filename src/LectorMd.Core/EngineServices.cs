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
        HybridTtsEngine engine)
    {
        Parser = parser;
        Chunking = chunking;
        Normalizer = normalizer;
        Cache = cache;
        Usage = usage;
        Engine = engine;
    }

    public MarkdownSpeechParser Parser { get; }
    public ChunkingOptions Chunking { get; }
    public SpeechTextNormalizer Normalizer { get; }
    public AudioCache Cache { get; }
    public UsageTracker Usage { get; }
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

        var engine = new HybridTtsEngine(azure, piper, usage, settings.OfflineOnly);
        return new EngineServices(parser, new ChunkingOptions(), normalizer, cache, usage, engine);
    }

    public SpeechDocument Parse(string markdown, string? sourcePath) => Parser.Parse(markdown, sourcePath);

    public IReadOnlyList<SpeechChunk> Plan(SpeechDocument document) => ChunkPlanner.Plan(document.Segments, Chunking);

    public void Dispose() => Engine.Dispose();
}
