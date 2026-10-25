using Shoken.Core.Coverage;
using Shoken.Core.Model;
using Shoken.Core.Parsing;
using Shoken.Core.Settings;

namespace Shoken.Core.Tests;

public class CoverageTests
{
    private static SpeechDocument Parse(string markdown, MarkdownSpeechOptions? options = null) =>
        new MarkdownSpeechParser(options).Parse(markdown, "prueba.md");

    private static string LongParagraph(int sentences) =>
        string.Join(" ", Enumerable.Repeat("Esta oración es lo bastante larga para que el párrafo se parta.", sentences));

    private static List<int> Heard(Action<HeardTracker> act)
    {
        var tracker = new HeardTracker();
        var heard = new List<int>();
        tracker.SegmentHeard += heard.Add;
        act(tracker);
        return heard;
    }

    [Fact]
    public void HeardTracker_CountsASegmentWhenPlaybackMovesToTheNextOne()
    {
        Assert.Equal([3], Heard(t => { t.Started(3, true); t.Moved(4); }));
    }

    [Fact]
    public void HeardTracker_DoesNotCountTheFirstSegment_WhenStartedInTheMiddle()
    {
        Assert.Equal([4], Heard(t => { t.Started(3, false); t.Moved(4); t.Moved(5); }));
    }

    [Fact]
    public void HeardTracker_DoesNotCountASegmentSkippedWithTenSeconds()
    {
        Assert.Equal([4], Heard(t => { t.Started(3, true); t.Jumped(3); t.Moved(4); t.Moved(5); }));
        Assert.Equal([6], Heard(t => { t.Started(3, true); t.Jumped(5); t.Moved(6); t.Moved(7); }));
    }

    [Fact]
    public void HeardTracker_DoesNotCountTheLandedSegment_WhenATickStillReadsTheOldPosition()
    {
        // Tras +10 s, el siguiente tick lee la posición vieja (4) y luego la nueva (5): el 5 no empezó desde su principio.
        Assert.Equal([6], Heard(t => { t.Started(3, true); t.Jumped(5); t.Moved(4); t.Moved(5); t.Moved(6); t.Moved(7); }));
    }

    [Fact]
    public void HeardTracker_CountsTheLastSegment_WhenTheDocumentEnds()
    {
        Assert.Equal([8], Heard(t => { t.Started(8, true); t.Finished(); }));
    }

    [Fact]
    public void HeardTracker_DoesNotCount_AfterStopping()
    {
        Assert.Empty(Heard(t => { t.Started(2, true); t.Reset(); t.Moved(3); }));
    }

    [Fact]
    public void Coverage_BlockIsHeardOnlyWhenAllItsPartsWereHeard()
    {
        var doc = Parse(LongParagraph(12), new MarkdownSpeechOptions { LongSegmentChars = 80 });
        Assert.True(doc.Segments.Count >= 3);
        int last = doc.Segments.Count - 1;
        var coverage = new DocumentCoverage(doc, []);

        for (int i = 0; i < last; i++)
        {
            Assert.False(coverage.MarkSegmentHeard(i));
        }

        Assert.False(coverage.IsBlockHeard(doc.Segments[0].AnchorId));
        Assert.True(coverage.MarkSegmentHeard(last));
        Assert.True(coverage.IsBlockHeard(doc.Segments[0].AnchorId));
        Assert.Equal(Enumerable.Range(0, last + 1), coverage.HeardSegments.Order());
        Assert.False(coverage.MarkSegmentHeard(last)); // ya estaba escuchado
    }

    [Fact]
    public void Coverage_StaysHeard_AfterReopeningTheSameDocument()
    {
        var doc = Parse("# A\n\nUno.\n\nDos.");
        var coverage = new DocumentCoverage(doc, []);
        coverage.MarkSegmentHeard(1);

        var reopened = new DocumentCoverage(Parse("# A\n\nUno.\n\nDos."), coverage.ToStored());

        Assert.True(reopened.IsBlockHeard(doc.Segments[1].AnchorId));
        Assert.False(reopened.IsBlockHeard(doc.Segments[2].AnchorId));
    }

    [Fact]
    public void Coverage_IsLost_OnlyForTheEditedBlock()
    {
        var v1 = Parse("Uno.\n\nDos.");
        var coverage = new DocumentCoverage(v1, []);
        coverage.MarkSegmentHeard(0);
        coverage.MarkSegmentHeard(1);

        var v2 = Parse("Uno.\n\nDos editado.");
        var reopened = new DocumentCoverage(v2, coverage.ToStored());

        Assert.True(reopened.IsBlockHeard(v2.Segments[0].AnchorId));
        Assert.False(reopened.IsBlockHeard(v2.Segments[1].AnchorId));
    }

    [Fact]
    public void Coverage_SurvivesMovingABlock()
    {
        var v1 = Parse("Uno.\n\nDos.\n\nTres.");
        var coverage = new DocumentCoverage(v1, []);
        coverage.MarkSegmentHeard(0);

        var v2 = Parse("Dos.\n\nTres.\n\nUno.");
        var reopened = new DocumentCoverage(v2, coverage.ToStored());

        Assert.True(reopened.IsBlockHeard(v2.Segments[2].AnchorId));
        Assert.Equal([2], reopened.HeardSegments);
    }

    [Fact]
    public void Coverage_StoresAnnouncement_ForAnAnnouncedCodeBlock()
    {
        var doc = Parse("Texto.\n\n```csharp\nint x = 1;\n```");
        var coverage = new DocumentCoverage(doc, []);
        foreach (SpeechSegment s in doc.Segments)
        {
            coverage.MarkSegmentHeard(s.Id);
        }

        var stored = coverage.ToStored();

        Assert.Equal(2, stored.Count);
        Assert.Equal(HeardSpoken.Full, stored.Single(b => b.Key == doc.Segments[0].BlockKey).Spoken);
        Assert.Equal(HeardSpoken.Announcement, stored.Single(b => b.Key == doc.Segments[^1].BlockKey).Spoken);
    }

    [Fact]
    public void Coverage_KeepsTheStoredSpoken_ForABlockNotHeardAgain()
    {
        // Sonó solo su anuncio; ahora las opciones lo leerían completo, pero no se volvió a escuchar.
        var doc = Parse("```csharp\nint x = 1;\n```", new MarkdownSpeechOptions { CodeBlocks = CodeBlockMode.Read });
        Assert.False(doc.Segments[0].IsAnnouncement);
        string key = doc.Segments[0].BlockKey;
        var coverage = new DocumentCoverage(doc, [new HeardBlock(key, HeardSpoken.Announcement)]);

        Assert.Equal([new HeardBlock(key, HeardSpoken.Announcement)], coverage.ToStored());

        foreach (SpeechSegment s in doc.Segments)
        {
            coverage.MarkSegmentHeard(s.Id); // escuchado ahora: vale cómo suena con las opciones de ahora
        }

        Assert.Equal([new HeardBlock(key, HeardSpoken.Full)], coverage.ToStored());
    }

    [Fact]
    public void Coverage_Clear_ForgetsEverything()
    {
        var doc = Parse("Uno.");
        var coverage = new DocumentCoverage(doc, [new HeardBlock(doc.Segments[0].BlockKey, HeardSpoken.Full)]);
        Assert.True(coverage.IsBlockHeard(doc.Segments[0].AnchorId));

        coverage.Clear();

        Assert.Empty(coverage.HeardAnchors);
        Assert.Empty(coverage.ToStored());
    }

    [Fact]
    public void CoverageStore_RoundTrips_AndTreatsADamagedFileAsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoken-cov-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoverageStore(dir);
            string doc = Path.Combine(dir, "a.md");
            var blocks = new[] { new HeardBlock("abc", HeardSpoken.Full), new HeardBlock("def", HeardSpoken.Announcement) };

            Assert.Empty(store.Load(doc));
            store.Save(doc, blocks);
            Assert.Equal(blocks, store.Load(doc));

            File.WriteAllText(DocumentFiles.PathFor(dir, doc), "{ no es json");
            Assert.Empty(store.Load(doc));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void CoverageStore_SkipsNullBlocks_InADamagedFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoken-cov-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoverageStore(dir);
            string doc = Path.Combine(dir, "a.md");
            Directory.CreateDirectory(dir);
            File.WriteAllText(DocumentFiles.PathFor(dir, doc),
                """{ "Document": "a.md", "Blocks": [null, { "Key": "abc", "Spoken": "Full" }, { "Spoken": "Full" }] }""");

            IReadOnlyList<HeardBlock> loaded = store.Load(doc);

            Assert.Equal([new HeardBlock("abc", HeardSpoken.Full)], loaded);
            Assert.Empty(new DocumentCoverage(Parse("Uno."), loaded).HeardAnchors); // no lanza
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void CoverageStore_SavingNothing_DeletesTheFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shoken-cov-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CoverageStore(dir);
            string doc = Path.Combine(dir, "a.md");
            store.Save(doc, [new HeardBlock("abc", HeardSpoken.Full)]);
            Assert.True(File.Exists(DocumentFiles.PathFor(dir, doc)));

            store.Save(doc, []);

            Assert.False(File.Exists(DocumentFiles.PathFor(dir, doc)));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void DocumentFiles_GivesNotesTheSameFileNameAsBefore()
    {
        // Valor que calculaba NoteStore.FileFor antes del cambio: SHA-256 de "C:\DOCS\A.MD", 20 hex.
        Assert.Equal(Path.Combine("datos", "b2828e32c9f48d4d7eb9.json"), DocumentFiles.PathFor("datos", @"C:\docs\a.md"));
    }
}
