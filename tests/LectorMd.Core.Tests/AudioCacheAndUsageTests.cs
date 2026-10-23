using LectorMd.Core.Audio;
using LectorMd.Core.Caching;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Tts;
using LectorMd.Core.Usage;

namespace LectorMd.Core.Tests;

public sealed class AudioCacheAndUsageTests : IDisposable
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
    public void Wav_RoundTripsAndConcatenatesWithSilence()
    {
        var a = new WavAudio(22050, 1, 16, new byte[22050 * 2]); // 1 s
        var b = new WavAudio(22050, 1, 16, new byte[22050]);     // 0.5 s

        var combined = WavAudio.Concatenate([(a, 0), (b, 250)], out var starts);
        var reparsed = WavAudio.Parse(combined.ToBytes());

        Assert.Equal(TimeSpan.Zero, starts[0]);
        Assert.Equal(1.25, starts[1].TotalSeconds, 3);
        Assert.Equal(1.75, reparsed.Duration.TotalSeconds, 3);
        Assert.Equal(22050, reparsed.SampleRate);
    }

    [Fact]
    public void Wav_RejectsNonWavData()
    {
        Assert.Throws<InvalidDataException>(() => WavAudio.Parse("hola mundo, no soy un wav"u8));
    }

    [Fact]
    public void Cache_StoresAndMapsRelativeMarksToSegmentIds()
    {
        var doc = new MarkdownSpeechParser().Parse("Uno.\n\nDos.\n\nTres.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments.Skip(1).ToList() };
        var cache = new AudioCache(_dir);
        string key = AudioCache.ComputeKey("prueba", "x");

        cache.Store(key, [1, 2, 3], ".mp3", [(0, TimeSpan.Zero), (1, TimeSpan.FromSeconds(2))], chunk, "motor");
        var hit = cache.TryGet(key, chunk, "motor");

        Assert.NotNull(hit);
        Assert.True(hit!.FromCache);
        Assert.Equal(new[] { 1, 2 }, hit.Marks.Select(m => m.SegmentId));
        Assert.Equal(2, hit.SegmentAt(TimeSpan.FromSeconds(2.5)));
        Assert.Equal(1, hit.SegmentAt(TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void Cache_RoundTripsDuration()
    {
        var doc = new MarkdownSpeechParser().Parse("Uno.\n\nDos.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments };
        var cache = new AudioCache(_dir);
        string key = AudioCache.ComputeKey("duracion");

        AudioChunk stored = cache.Store(key, [1], ".mp3", [(0, TimeSpan.Zero), (1, TimeSpan.FromSeconds(4))], chunk, "m",
            duration: TimeSpan.FromSeconds(12.5));
        AudioChunk? hit = cache.TryGet(key, chunk, "m");

        Assert.Equal(TimeSpan.FromSeconds(12.5), stored.Duration);
        Assert.Equal(TimeSpan.FromSeconds(12.5), hit!.Duration);
    }

    [Fact]
    public void Cache_OldEntriesWithoutDuration_StillLoad()
    {
        var doc = new MarkdownSpeechParser().Parse("Uno.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments };
        var cache = new AudioCache(_dir);
        string key = AudioCache.ComputeKey("vieja");
        File.WriteAllText(Path.Combine(_dir, key + ".json"), """{"Extension":".mp3","Marks":[{"Index":0,"OffsetTicks":0}]}""");
        File.WriteAllBytes(Path.Combine(_dir, key + ".mp3"), [1]);

        AudioChunk? hit = cache.TryGet(key, chunk, "m");

        Assert.NotNull(hit);
        Assert.Null(hit!.Duration);
    }

    [Fact]
    public void Cache_FillsMissingMarks()
    {
        var doc = new MarkdownSpeechParser().Parse("Uno.\n\nDos.\n\nTres.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments };
        var cache = new AudioCache(_dir);

        var audio = cache.Store(AudioCache.ComputeKey("k"), [0], ".wav", [(0, TimeSpan.Zero), (2, TimeSpan.FromSeconds(3))], chunk, "m");

        Assert.Equal(3, audio.Marks.Count);
        Assert.Equal(TimeSpan.Zero, audio.OffsetOf(1));
    }

    [Fact]
    public void Usage_AccumulatesAndResetsEachMonth()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        string path = Path.Combine(_dir, "uso.json");
        Directory.CreateDirectory(_dir);

        var usage = new UsageTracker(path, 1000, () => now);
        usage.Add(600);
        Assert.True(usage.CanSpend(400));
        Assert.False(usage.CanSpend(401));

        var reloaded = new UsageTracker(path, 1000, () => now);
        Assert.Equal(600, reloaded.Used);

        now = new DateTime(2026, 11, 1, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(0, reloaded.Used);
        Assert.True(reloaded.CanSpend(1000));
    }

    [Fact]
    public void Usage_ServiceExhaustionBlocksUntilNextMonth()
    {
        var now = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_dir);
        var usage = new UsageTracker(Path.Combine(_dir, "uso.json"), 1000, () => now);

        usage.MarkServiceExhausted();
        Assert.True(usage.IsExhausted);
        Assert.False(usage.CanSpend(1));

        now = now.AddMonths(1);
        Assert.False(usage.IsExhausted);
    }
}
