using Shoken.Core.Caching;
using Shoken.Core.Parsing;
using Shoken.Core.Planning;
using Shoken.Core.Tts;

namespace Shoken.Core.Tests;

public sealed class WordTimelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shoken-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SpeechChunk Chunk(string markdown)
    {
        var doc = new MarkdownSpeechParser().Parse(markdown);
        return new SpeechChunk { Index = 0, Segments = doc.Segments };
    }

    private static AudioChunk Audio(SpeechChunk chunk, TimeSpan[] marks, TimeSpan? duration, IReadOnlyList<WordMark>? words = null) => new()
    {
        FilePath = "x.mp3",
        Marks = chunk.Segments.Select((s, i) => new SegmentMark(s.Id, marks[i])).ToList(),
        EngineName = "m",
        Duration = duration,
        Words = words ?? [],
    };

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void WordTimeline_Tokenize_DropsSurroundingPunctuation()
    {
        Assert.Equal(["Pulsa", "Guardar", "y", "appsettings.json", "C", "5"],
            WordTimeline.Tokenize("Pulsa «Guardar», y (appsettings.json) — C#: 5."));
    }

    [Fact]
    public void WordTimeline_WithoutEngineWords_SpreadsTheSegmentByLength()
    {
        SpeechChunk chunk = Chunk("aa bbbb aa.");
        WordTimeline timeline = WordTimeline.Build(chunk, Audio(chunk, [S(1)], S(2)));

        // Pesos: largo + 1 → 3, 5, 3 sobre 1 s.
        IReadOnlyList<TimeSpan> starts = timeline.StartsOf(chunk.Segments[0].Id);
        Assert.Equal(3, starts.Count);
        Assert.Equal(1.0, starts[0].TotalSeconds, 3);
        Assert.Equal(1 + 3 / 11.0, starts[1].TotalSeconds, 3);
        Assert.Equal(1 + 8 / 11.0, starts[2].TotalSeconds, 3);

        Assert.Equal(-1, timeline.WordAt(chunk.Segments[0].Id, S(0.5)));
        Assert.Equal(1, timeline.WordAt(chunk.Segments[0].Id, S(1.5)));
        Assert.Equal(2, timeline.WordAt(chunk.Segments[0].Id, S(5)));
    }

    [Fact]
    public void WordTimeline_EndsEachSegmentBeforeThePauseOfTheNext()
    {
        SpeechChunk chunk = Chunk("# Título largo\n\nUno dos.");
        int gap = chunk.GapBefore(1);
        Assert.True(gap > 0);

        WordTimeline timeline = WordTimeline.Build(chunk, Audio(chunk, [S(0), S(3)], S(5)));
        TimeSpan speechEnd = S(3) - TimeSpan.FromMilliseconds(gap);
        IReadOnlyList<TimeSpan> title = timeline.StartsOf(chunk.Segments[0].Id);
        Assert.True(title[^1] < speechEnd);
        Assert.Equal(3.0, timeline.StartsOf(chunk.Segments[1].Id)[0].TotalSeconds, 3);
    }

    [Fact]
    public void WordTimeline_UsesEngineWords_AndSpreadsTheOnesWithoutMatch()
    {
        SpeechChunk chunk = Chunk("Pulsa «Guardar» en appsettings.json ya.");
        int id = chunk.Segments[0].Id;
        // Azure parte «appsettings.json» y dice las palabras con su texto, sin comillas.
        WordMark[] words =
        [
            new(id, S(0.1), "Pulsa"), new(id, S(0.5), "Guardar"), new(id, S(1.0), "en"),
            new(id, S(1.2), "appsettings"), new(id, S(1.8), "json"), new(id, S(2.4), "ya"),
        ];

        WordTimeline timeline = WordTimeline.Build(chunk, Audio(chunk, [S(0)], S(3), words));

        Assert.Equal([S(0.1), S(0.5), S(1.0), S(1.2), S(2.4)], timeline.StartsOf(id));
    }

    [Fact]
    public void WordTimeline_InterpolatesBetweenMatchedWords()
    {
        SpeechChunk chunk = Chunk("uno SQL tres.");
        int id = chunk.Segments[0].Id;
        // «SQL» se dijo como otra cosa (diccionario): queda entre sus vecinas.
        WordMark[] words = [new(id, S(1), "uno"), new(id, S(1.4), "esecuele"), new(id, S(2), "tres")];

        IReadOnlyList<TimeSpan> starts = WordTimeline.Build(chunk, Audio(chunk, [S(0.5)], S(3), words)).StartsOf(id);

        Assert.Equal(S(1), starts[0]);
        Assert.True(starts[1] > S(1) && starts[1] < S(2));
        Assert.Equal(S(2), starts[2]);
    }

    [Fact]
    public void WordTimeline_WithoutDuration_EstimatesTheLastSegment()
    {
        SpeechChunk chunk = Chunk("Una frase cualquiera para estimar.");
        IReadOnlyList<TimeSpan> starts = WordTimeline.Build(chunk, Audio(chunk, [S(2)], null)).StartsOf(chunk.Segments[0].Id);

        Assert.Equal(5, starts.Count);
        Assert.True(starts.Zip(starts.Skip(1)).All(p => p.First < p.Second));
    }

    [Fact]
    public void AzureWords_GoToTheSegmentOfTheLastBookmarkBeforeThem()
    {
        var assigned = AzureTtsEngine.AssignWords(
            [(0, S(0)), (1, S(2))],
            [(S(2.5), "dos"), (S(0.2), "uno"), (S(1.9), "fin")]);

        Assert.Equal([(0, S(0.2), "uno"), (0, S(1.9), "fin"), (1, S(2.5), "dos")], assigned);
    }

    [Fact]
    public void Cache_RoundTripsWordTimes_WithoutChangingOldEntries()
    {
        var doc = new MarkdownSpeechParser().Parse("Uno.\n\nDos y tres.");
        var chunk = new SpeechChunk { Index = 0, Segments = doc.Segments.Skip(1).ToList() };
        var cache = new AudioCache(_dir);
        string key = AudioCache.ComputeKey("palabras");

        cache.Store(key, [1], ".mp3", [(0, TimeSpan.Zero)], chunk, "m", S(2), [(0, S(0.1), "Dos"), (0, S(0.6), "y")]);
        AudioChunk? hit = cache.TryGet(key, chunk, "m");

        Assert.Equal([new WordMark(1, S(0.1), "Dos"), new WordMark(1, S(0.6), "y")], hit!.Words);

        string old = AudioCache.ComputeKey("vieja");
        cache.Store(old, [1], ".mp3", [(0, TimeSpan.Zero)], chunk, "m");
        Assert.Empty(cache.TryGet(old, chunk, "m")!.Words);
    }
}
