using LectorMd.Core.Caching;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;

namespace LectorMd.Core.Tests;

public sealed class KokoroTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lectormd-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Phonemes_BecomeMexicanSpanish_WithSeseoAndYeismo()
    {
        // Kokoro fonetiza como en España: "quince" con θ y "calle" con ʎ.
        Assert.Equal("kˈaʝe sˈinko sˈesar", KokoroPhonemes.ToMexican("kˈaʎe θˈinko θˈesar"));
    }

    [Fact]
    public void Tokens_AreSplitAtPunctuation_ThenAtSpaces_WhenTooLongForOnePass()
    {
        // 1 = puntuación, 0 = espacio, el resto son fonemas.
        int[] tokens = [5, 5, 1, 5, 5, 0, 5, 5, 5, 0, 5, 5];

        IReadOnlyList<int[]> pieces = KokoroPhonemes.Split(tokens, max: 5, isPunctuation: t => t == 1, isSpace: t => t == 0);

        Assert.Equal([[5, 5, 1], [5, 5, 0], [5, 5, 5, 0], [5, 5]], pieces);
        Assert.Equal([[5, 5]], KokoroPhonemes.Split([5, 5], max: 5, t => t == 1, t => t == 0));
        Assert.Equal([[5, 5, 5], [5, 5]], KokoroPhonemes.Split([5, 5, 5, 5, 5], max: 3, t => t == 1, t => t == 0)); // sin dónde cortar
    }

    /// <summary>Backend falso: "fonetiza" devolviendo el texto con θ y ʎ, y genera 10 muestras por token.</summary>
    private sealed class FakeBackend : IKokoroBackend
    {
        public List<string> Phonemes { get; } = [];
        public List<int> InferLengths { get; } = [];

        public int SampleRate => 1000;
        public int MaxTokens { get; init; } = 500;

        public string Phonemize(string text) => "θʎ " + text;

        public int[] Tokenize(string phonemes)
        {
            Phonemes.Add(phonemes);
            return phonemes.Select(c => c == ' ' ? 0 : c == '.' ? 1 : 2).ToArray();
        }

        public bool IsPunctuation(int token) => token == 1;

        public bool IsSpace(int token) => token == 0;

        public float[] Infer(int[] tokens, float speed)
        {
            InferLengths.Add(tokens.Length);
            return new float[tokens.Length * 10];
        }
    }

    private (KokoroTtsEngine Engine, FakeBackend Backend) Engine(int maxTokens = 500, bool modelPresent = true)
    {
        Directory.CreateDirectory(_dir);
        string model = Path.Combine(_dir, "kokoro.onnx");
        if (modelPresent)
        {
            File.WriteAllText(model, "modelo");
        }

        File.WriteAllText(Path.Combine(_dir, "ef_dora.npy"), "voz"); // la voz la trae la app

        var backend = new FakeBackend { MaxTokens = maxTokens };
        var options = new KokoroTtsOptions { ModelPath = model, Voice = "ef_dora", VoicesDirectory = _dir };
        var engine = new KokoroTtsEngine(options, new SpeechTextNormalizer(), new AudioCache(Path.Combine(_dir, "cache")), _ => backend);
        return (engine, backend);
    }

    private static SpeechChunk Chunk(string markdown) =>
        new() { Index = 0, Segments = new MarkdownSpeechParser().Parse(markdown).Segments };

    [Fact]
    public async Task Kokoro_ReadsEachSegmentInMexicanSpanish_AndMarksWhereEachStarts()
    {
        (KokoroTtsEngine engine, FakeBackend backend) = Engine();
        SpeechChunk chunk = Chunk("Uno.\n\nDos dos.");

        AudioChunk audio = await engine.SynthesizeAsync(chunk, CancellationToken.None);

        Assert.All(backend.Phonemes, p => Assert.StartsWith("sʝ ", p)); // seseo y yeísmo antes de sintetizar
        Assert.Equal(2, audio.Marks.Count);
        Assert.True(audio.Marks[1].Offset > audio.Marks[0].Offset, "el segundo segmento empieza después del primero");
        Assert.Equal("Kokoro · Dora", audio.EngineName);
        Assert.True(File.Exists(audio.FilePath));
    }

    [Fact]
    public async Task Kokoro_ReusesTheCache_WithoutSynthesizingAgain()
    {
        (KokoroTtsEngine engine, FakeBackend backend) = Engine();
        SpeechChunk chunk = Chunk("Hola mundo.");
        await engine.SynthesizeAsync(chunk, CancellationToken.None);
        int calls = backend.InferLengths.Count;

        AudioChunk again = await engine.SynthesizeAsync(chunk, CancellationToken.None);

        Assert.True(again.FromCache);
        Assert.Equal(calls, backend.InferLengths.Count);
        Assert.NotNull(engine.TryGetCached(chunk));
    }

    [Fact]
    public async Task Kokoro_SplitsALongSegment_SoNoPassExceedsTheModelLimit()
    {
        (KokoroTtsEngine engine, FakeBackend backend) = Engine(maxTokens: 12);

        await engine.SynthesizeAsync(Chunk("Una oración bastante larga para cortar. Y otra más."), CancellationToken.None);

        Assert.True(backend.InferLengths.Count > 1);
        Assert.All(backend.InferLengths, length => Assert.True(length <= 12, $"pasada de {length} tokens"));
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task Download_WritesTheModel_ReportsProgress_AndLeavesNoPartialFile()
    {
        string target = Path.Combine(_dir, "kokoro", "kokoro.onnx");
        var reported = new List<double>();
        using var http = new HttpClient(new BytesHandler(new byte[5000]));

        await KokoroModelDownloader.DownloadAsync(http, new Uri("https://ejemplo/kokoro.onnx"), target, expectedBytes: 5000,
            new Progress<double>(reported.Add), CancellationToken.None);

        Assert.Equal(5000, new FileInfo(target).Length);
        Assert.False(File.Exists(target + ".part"));
        await Task.Delay(50); // Progress<T> avisa en el contexto de sincronización
        Assert.Contains(1.0, reported);
    }

    [Fact]
    public async Task Download_WithTheWrongSize_KeepsNoBrokenModel()
    {
        string target = Path.Combine(_dir, "kokoro", "kokoro.onnx");
        using var http = new HttpClient(new BytesHandler(new byte[100]));

        await Assert.ThrowsAsync<IOException>(() => KokoroModelDownloader.DownloadAsync(http, new Uri("https://ejemplo/kokoro.onnx"),
            target, expectedBytes: 5000, null, CancellationToken.None));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Kokoro_WithoutTheModel_IsNotConfigured()
    {
        (KokoroTtsEngine engine, _) = Engine(modelPresent: false);

        Assert.False(engine.IsConfigured);
        TtsException error = await Assert.ThrowsAsync<TtsException>(() => engine.SynthesizeAsync(Chunk("Hola."), CancellationToken.None));
        Assert.Equal(TtsFailureKind.NotConfigured, error.Kind);
    }
}
