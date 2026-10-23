using LectorMd.Core.Model;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Tts;

namespace LectorMd.Core.Tests;

public class ReadingTimelineTests
{
    private static SpeechSegment Segment(int id, string text, int pauseAfterMs = 0) => new()
    {
        Id = id,
        AnchorId = "s" + id,
        Kind = SegmentKind.Paragraph,
        Runs = [new SpeechRun(text)],
        PauseAfterMs = pauseAfterMs,
    };

    private static SpeechDocument Document(params SpeechSegment[] segments) =>
        new() { Title = "Prueba", Segments = segments, HtmlBody = "" };

    private static ReadingTimeline Build(string markdown)
    {
        SpeechDocument doc = new MarkdownSpeechParser().Parse(markdown);
        return ReadingTimeline.Build(doc, ChunkPlanner.Plan(doc.Segments), _ => null);
    }

    [Fact]
    public void Estimate_UsesCharactersAndPauses()
    {
        SpeechDocument doc = Document(Segment(0, new string('a', 150), pauseAfterMs: 300));
        var chunks = new[] { new SpeechChunk { Index = 0, Segments = doc.Segments } };

        ReadingTimeline timeline = ReadingTimeline.Build(doc, chunks, _ => null);

        Assert.Equal(TimeSpan.FromSeconds(10.3), timeline.DurationOf(0));
        Assert.True(timeline.Chapters.Single().IsEstimated);
    }

    [Fact]
    public void CachedChunk_UsesRealMarksAndDuration()
    {
        SpeechDocument doc = Document(Segment(0, "Uno."), Segment(1, "Dos."));
        var chunks = new[] { new SpeechChunk { Index = 0, Segments = doc.Segments } };
        var audio = new AudioChunk
        {
            FilePath = "x.mp3",
            EngineName = "Azure",
            Marks = [new SegmentMark(0, TimeSpan.Zero), new SegmentMark(1, TimeSpan.FromSeconds(4))],
            Duration = TimeSpan.FromSeconds(9),
        };

        ReadingTimeline timeline = ReadingTimeline.Build(doc, chunks, _ => audio);

        Assert.Equal(TimeSpan.FromSeconds(4), timeline.DurationOf(0));
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.DurationOf(1));
        Assert.Equal(TimeSpan.FromSeconds(9), timeline.Total);
        Assert.False(timeline.Chapters.Single().IsEstimated);
    }

    [Fact]
    public void MixedChunks_UseRealDurationWhereCached_AndEstimateTheRest()
    {
        SpeechDocument doc = Document(Segment(0, "Uno."), Segment(1, new string('b', 30)));
        var first = new SpeechChunk { Index = 0, Segments = [doc.Segments[0]] };
        var second = new SpeechChunk { Index = 1, Segments = [doc.Segments[1]] };
        var cachedFirst = new AudioChunk
        {
            FilePath = "x.mp3",
            EngineName = "Azure",
            Marks = [new SegmentMark(0, TimeSpan.Zero)],
            Duration = TimeSpan.FromSeconds(7),
        };

        ReadingTimeline timeline = ReadingTimeline.Build(doc, [first, second], chunk => chunk == first ? cachedFirst : null);

        Assert.Equal(TimeSpan.FromSeconds(7), timeline.DurationOf(0));
        Assert.Equal(TimeSpan.FromSeconds(7), timeline.StartOf(1));
        Assert.Equal(TimeSpan.FromSeconds(2), timeline.DurationOf(1)); // 30 caracteres a 15 por segundo
        Assert.Equal(TimeSpan.FromSeconds(9), timeline.Total);
        Assert.True(timeline.Chapters.Single().IsEstimated);
    }

    [Fact]
    public void CachedDurationShorterThanTheLastMark_NeverGivesNegativeDurations()
    {
        SpeechDocument doc = Document(Segment(0, "Uno."), Segment(1, "Dos."));
        var chunks = new[] { new SpeechChunk { Index = 0, Segments = doc.Segments } };
        var audio = new AudioChunk
        {
            FilePath = "x.mp3",
            EngineName = "Azure",
            Marks = [new SegmentMark(0, TimeSpan.Zero), new SegmentMark(1, TimeSpan.FromSeconds(5))],
            Duration = TimeSpan.FromSeconds(3),
        };

        ReadingTimeline timeline = ReadingTimeline.Build(doc, chunks, _ => audio);

        Assert.Equal(TimeSpan.Zero, timeline.DurationOf(1));
        Assert.True(timeline.Total >= TimeSpan.Zero);
    }

    [Fact]
    public void Chapters_FollowTheOutline_AndAddInicioOnlyIfItHasContent()
    {
        Assert.Equal(["Inicio", "A", "B"], Build("Intro.\n\n# A\n\nTexto.\n\n# B\n\nMás.").Chapters.Select(c => c.Title));
        Assert.Equal(["A", "B"], Build("# A\n\nTexto.\n\n# B\n\nMás.").Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Chapters_AreContiguousAndCoverTheWholeDocument()
    {
        ReadingTimeline timeline = Build("Intro.\n\n# A\n\nTexto.\n\n## A1\n\nMás.\n\n# B\n\nFin.");

        TimeSpan expectedStart = TimeSpan.Zero;
        foreach (TimelineChapter chapter in timeline.Chapters)
        {
            Assert.Equal(expectedStart, chapter.Start);
            expectedStart += chapter.Duration;
        }

        Assert.Equal(timeline.Total, expectedStart);
    }

    [Fact]
    public void NoHeadings_IsOneChapterNamedDocumento()
    {
        ReadingTimeline timeline = Build("Solo texto.");

        TimelineChapter chapter = Assert.Single(timeline.Chapters);
        Assert.Equal("Documento", chapter.Title);
        Assert.Equal(TimeSpan.Zero, chapter.Start);
        Assert.Equal(timeline.Total, chapter.Duration);
    }

    [Fact]
    public void ChapterIndexOf_FindsTheChapterOfASegment()
    {
        SpeechDocument doc = new MarkdownSpeechParser().Parse("Intro.\n\n# A\n\nTexto a.\n\n# B\n\nTexto b.");
        ReadingTimeline timeline = ReadingTimeline.Build(doc, ChunkPlanner.Plan(doc.Segments), _ => null);
        int textB = doc.Segments.Single(s => s.PlainText.StartsWith("Texto b", StringComparison.Ordinal)).Id;

        Assert.Equal(0, timeline.ChapterIndexOf(0));
        Assert.Equal(2, timeline.ChapterIndexOf(textB));
    }

    [Fact]
    public void ElapsedAt_IsStartPlusClampedOffset()
    {
        ReadingTimeline timeline = Build("# A\n\nUno.\n\nDos.");
        int id = 1;

        Assert.Equal(timeline.StartOf(id) + timeline.DurationOf(id), timeline.ElapsedAt(id, TimeSpan.FromHours(1)));
        Assert.Equal(timeline.StartOf(id), timeline.ElapsedAt(id, TimeSpan.FromSeconds(-3)));
    }
}
