using LectorMd.Core.Planning;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tests;

public sealed class HybridChainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lectormd-tests-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Motor falso: responde con su nombre o falla con el error que se le indique.</summary>
    private sealed class FakeEngine(string name) : ITtsEngine
    {
        public TtsFailureKind? FailWith { get; set; }
        public AudioChunk? Cached { get; set; }
        public int Calls { get; private set; }

        public string Name => name;
        public bool IsConfigured { get; set; } = true;

        public AudioChunk? TryGetCached(SpeechChunk chunk) => Cached;

        public Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailWith is { } kind)
            {
                throw new TtsException(kind, $"{name} falló");
            }

            return Task.FromResult(Audio(name));
        }
    }

    private static AudioChunk Audio(string engine, bool fromCache = false) =>
        new() { FilePath = engine + ".mp3", Marks = [], EngineName = engine, FromCache = fromCache };

    private static readonly SpeechChunk Chunk = new() { Index = 0, Segments = [] };

    private UsageTracker Usage(string name, int limit = 1000) => new(Path.Combine(_dir, name + ".json"), limit, () => _now);

    private (HybridTtsEngine Hybrid, FakeEngine Azure, FakeEngine Google, FakeEngine Piper) Chain(
        int azureLimit = 1000, int googleLimit = 1000)
    {
        var azure = new FakeEngine("Azure");
        var google = new FakeEngine("Google");
        var piper = new FakeEngine("Piper");
        var hybrid = new HybridTtsEngine(
            [new CloudEngine(azure, Usage("azure", azureLimit), "Azure"), new CloudEngine(google, Usage("google", googleLimit), "Google")],
            piper, offlineOnly: false, utcNow: () => _now);
        return (hybrid, azure, google, piper);
    }

    [Fact]
    public async Task AzureWorks_UsesAzure()
    {
        (HybridTtsEngine hybrid, _, FakeEngine google, _) = Chain();

        AudioChunk audio = await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal("Azure", audio.EngineName);
        Assert.False(hybrid.Status.UsingFallback);
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task AzureNetworkFailure_FallsBackToGoogle_WithAzureReason()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, _, FakeEngine piper) = Chain();
        azure.FailWith = TtsFailureKind.Network;

        AudioChunk audio = await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal("Google", audio.EngineName);
        Assert.True(hybrid.Status.UsingFallback);
        Assert.Contains("Azure falló", hybrid.Status.Reason);
        Assert.Equal(0, piper.Calls);
    }

    [Fact]
    public async Task AzureAndGoogleWithoutQuota_UsePiper()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, FakeEngine google, _) = Chain(azureLimit: 0, googleLimit: 0);

        AudioChunk audio = await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal("Piper", audio.EngineName);
        Assert.Equal(0, azure.Calls);
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task GoogleRejectsTheKey_IsNotCalledAgain()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, FakeEngine google, _) = Chain();
        azure.FailWith = TtsFailureKind.Authentication;
        google.FailWith = TtsFailureKind.Authentication;

        await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);
        await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal(1, azure.Calls);
        Assert.Equal(1, google.Calls);
    }

    [Fact]
    public async Task AzureAndGoogleFail_TheReasonMentionsBoth()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, FakeEngine google, _) = Chain();
        azure.FailWith = TtsFailureKind.Network;
        google.FailWith = TtsFailureKind.Authentication;

        await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Contains("Azure falló", hybrid.Status.Reason);
        Assert.Contains("Google falló", hybrid.Status.Reason);
    }

    [Fact]
    public async Task AzureComesBackAfterItsCooldown()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, _, _) = Chain();
        azure.FailWith = TtsFailureKind.Network;
        await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        azure.FailWith = null;
        _now = _now.AddMinutes(2);
        AudioChunk audio = await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal("Azure", audio.EngineName);
        Assert.False(hybrid.Status.UsingFallback);
    }

    [Fact]
    public async Task OnlyGoogleConfigured_IsThePrimary()
    {
        (HybridTtsEngine hybrid, FakeEngine azure, _, _) = Chain();
        azure.IsConfigured = false;

        AudioChunk audio = await hybrid.SynthesizeAsync(Chunk, CancellationToken.None);

        Assert.Equal("Google", audio.EngineName);
        Assert.False(hybrid.Status.UsingFallback);
    }

    [Fact]
    public void GoogleCache_IsUsedOnlyWhileAzureIsUnavailable()
    {
        (HybridTtsEngine hybrid, _, FakeEngine google, _) = Chain();
        google.Cached = Audio("Google", fromCache: true);
        Assert.Null(hybrid.TryGetCached(Chunk)); // Azure disponible: no se queda con la voz de respaldo

        (HybridTtsEngine noQuota, _, FakeEngine google2, _) = Chain(azureLimit: 0);
        google2.Cached = Audio("Google", fromCache: true);
        Assert.Equal("Google", noQuota.TryGetCached(Chunk)?.EngineName);
    }
}
