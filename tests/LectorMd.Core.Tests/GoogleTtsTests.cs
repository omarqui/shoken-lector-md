using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LectorMd.Core.Caching;
using LectorMd.Core.Model;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Ssml;
using LectorMd.Core.Text;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tests;

public sealed class GoogleTtsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lectormd-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ motor

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """{"audioContent":"AQID","timepoints":[{"markName":"0","timeSeconds":0},{"markName":"1","timeSeconds":1.25}]}""",
            Encoding.UTF8, "application/json"),
    };

    private (GoogleTtsEngine Engine, SpeechChunk Chunk, UsageTracker Usage) Create(FakeHandler handler, int limit = 100_000)
    {
        SpeechDocument doc = new MarkdownSpeechParser().Parse("# Título\n\nHola.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments };
        var usage = new UsageTracker(Path.Combine(_dir, "uso-google.json"), limit);
        var engine = new GoogleTtsEngine(
            new GoogleTtsOptions { ApiKey = "CLAVE", Voice = "es-US-Neural2-A" },
            new SsmlBuilder(new SpeechTextNormalizer()), new AudioCache(Path.Combine(_dir, "cache")), usage, new HttpClient(handler));
        return (engine, chunk, usage);
    }

    [Fact]
    public async Task Synthesize_SendsTheSsmlWithMarks_TheVoiceAndTimepointing()
    {
        var handler = new FakeHandler(_ => Ok());
        (GoogleTtsEngine engine, SpeechChunk chunk, _) = Create(handler);

        await engine.SynthesizeAsync(chunk, CancellationToken.None);

        (Uri uri, string body) = Assert.Single(handler.Requests);
        Assert.Equal("https://texttospeech.googleapis.com/v1beta1/text:synthesize?key=CLAVE", uri.AbsoluteUri);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Contains("<mark name=\"0\"", root.GetProperty("input").GetProperty("ssml").GetString());
        Assert.Equal("es-US", root.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal("es-US-Neural2-A", root.GetProperty("voice").GetProperty("name").GetString());
        Assert.Equal("MP3", root.GetProperty("audioConfig").GetProperty("audioEncoding").GetString());
        Assert.Equal("SSML_MARK", root.GetProperty("enableTimePointing")[0].GetString());
    }

    [Fact]
    public async Task Synthesize_CachesTheAudioWithItsMarks_AndCountsUsage()
    {
        var handler = new FakeHandler(_ => Ok());
        (GoogleTtsEngine engine, SpeechChunk chunk, UsageTracker usage) = Create(handler);

        AudioChunk audio = await engine.SynthesizeAsync(chunk, CancellationToken.None);
        AudioChunk again = await engine.SynthesizeAsync(chunk, CancellationToken.None);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(1.25)], audio.Marks.Select(m => m.Offset));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(audio.FilePath));
        Assert.True(again.FromCache);
        Assert.NotNull(engine.TryGetCached(chunk));
        Assert.Single(handler.Requests); // la segunda vez salió de la caché
        Assert.True(usage.Used > 0);
    }

    [Fact]
    public async Task Synthesize_WithoutLocalQuota_DoesNotCallGoogle()
    {
        var handler = new FakeHandler(_ => Ok());
        (GoogleTtsEngine engine, SpeechChunk chunk, _) = Create(handler, limit: 10);

        TtsException ex = await Assert.ThrowsAsync<TtsException>(() => engine.SynthesizeAsync(chunk, CancellationToken.None));

        Assert.Equal(TtsFailureKind.LocalLimitReached, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Synthesize_NetworkFailure_IsNetwork()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("sin red"));
        (GoogleTtsEngine engine, SpeechChunk chunk, _) = Create(handler);

        TtsException ex = await Assert.ThrowsAsync<TtsException>(() => engine.SynthesizeAsync(chunk, CancellationToken.None));

        Assert.Equal(TtsFailureKind.Network, ex.Kind);
    }

    [Fact]
    public void MapError_InvalidKeyComesAs400_AndIsAuthentication()
    {
        const string body = """
            {"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT",
             "details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID"}]}}
            """;

        Assert.Equal(TtsFailureKind.Authentication, GoogleTtsEngine.MapError(HttpStatusCode.BadRequest, body).Kind);
    }

    [Fact]
    public async Task Synthesize_UnexpectedSuccessBody_IsATtsError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        (GoogleTtsEngine engine, SpeechChunk chunk, _) = Create(handler);

        TtsException ex = await Assert.ThrowsAsync<TtsException>(() => engine.SynthesizeAsync(chunk, CancellationToken.None));

        Assert.Equal(TtsFailureKind.Other, ex.Kind);
    }

    [Fact]
    public async Task Synthesize_OnceSent_IsNotCancelled_AndIsCounted()
    {
        // Detener o saltar no aborta un pedido ya enviado: Google lo cobraría y la app no lo contaría.
        using var cancel = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cancel.Cancel(); // el usuario detiene justo cuando Google ya respondió
            byte[] body = Encoding.UTF8.GetBytes(
                """{"audioContent":"AQID","timepoints":[{"markName":"0","timeSeconds":0}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(body)) };
        });
        (GoogleTtsEngine engine, SpeechChunk chunk, UsageTracker usage) = Create(handler);

        await engine.SynthesizeAsync(chunk, cancel.Token);

        Assert.True(usage.Used > 0);
        Assert.NotNull(engine.TryGetCached(chunk));
    }

    [Fact]
    public void GoogleSsml_KeepsTheSpaceBetweenTwoStyledWords()
    {
        string azure = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"es-MX\"><voice name=\"v\">" +
                       "<prosody rate=\"-4%\">negrita</prosody> <prosody rate=\"-4%\">otra</prosody></voice></speak>";

        Assert.Contains("</prosody> <prosody", GoogleSsml.FromAzure(azure));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TtsFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Unauthorized, TtsFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, TtsFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, TtsFailureKind.Other)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TtsFailureKind.Network)]
    public void MapError_ClassifiesGoogleResponses(HttpStatusCode status, TtsFailureKind expected)
    {
        TtsException ex = GoogleTtsEngine.MapError(status, """{"error":{"code":0,"message":"detalle de Google"}}""");

        Assert.Equal(expected, ex.Kind);
        Assert.Contains("detalle de Google", ex.Message);
    }

    private const string Azure =
        "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"es-MX\">" +
        "<voice name=\"es-MX-DaliaNeural\"><break time=\"300ms\"/><bookmark mark=\"0\"/>" +
        "<prosody rate=\"-6%\" pitch=\"-3%\">Título.</prosody> <bookmark mark=\"1\"/>Texto con " +
        "<prosody rate=\"-4%\" pitch=\"+6%\">negrita</prosody> &amp; más.</voice></speak>";

    // ------------------------------------------------------------------ SSML

    [Fact]
    public void GoogleSsml_TurnsBookmarksIntoMarks_AndDropsTheVoice()
    {
        XElement speak = XElement.Parse(GoogleSsml.FromAzure(Azure));

        Assert.Equal("speak", speak.Name.LocalName);
        Assert.Equal(XNamespace.None, speak.Name.Namespace);
        Assert.Empty(speak.Descendants().Where(e => e.Name.LocalName is "voice" or "bookmark"));
        Assert.Equal(["0", "1"], speak.Descendants("mark").Select(m => (string?)m.Attribute("name")));
    }

    [Fact]
    public void GoogleSsml_ConvertsRelativeRateToPercentOfNormal_AndPitchToSemitones()
    {
        var prosody = XElement.Parse(GoogleSsml.FromAzure(Azure)).Descendants("prosody").ToList();

        Assert.Equal("94%", (string?)prosody[0].Attribute("rate"));
        Assert.Equal("-0.5st", (string?)prosody[0].Attribute("pitch"));
        Assert.Equal("96%", (string?)prosody[1].Attribute("rate"));
        Assert.Equal("+1st", (string?)prosody[1].Attribute("pitch"));
    }

    [Fact]
    public void GoogleSsml_KeepsBreaksTextAndEscaping()
    {
        string google = GoogleSsml.FromAzure(Azure);

        Assert.Contains("<break time=\"300ms\" />", google.Replace("<break time=\"300ms\"/>", "<break time=\"300ms\" />"));
        Assert.Contains("&amp; más.", google);
        Assert.Contains("Título.", google);
    }
}
