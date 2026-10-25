using Shoken.Core.Model;
using Shoken.Core.Parsing;

namespace Shoken.Core.Tests;

public class ResumeLocatorTests
{
    private static SpeechDocument Parse(string markdown, MarkdownSpeechOptions? options = null) =>
        new MarkdownSpeechParser(options).Parse(markdown, "prueba.md");

    [Fact]
    public void Resume_FindsTheSameParagraph_AfterTextWasInsertedBefore()
    {
        var v1 = Parse("# A\n\nUno.\n\nDos.\n\nTres.");
        var v2 = Parse("# A\n\nNuevo.\n\nUno.\n\nDos.\n\nTres.");

        var point = ResumePoint.Of(v1, 3);

        Assert.Equal(4, ResumeLocator.Find(v2, point));
    }

    [Fact]
    public void Resume_KeepsThePartWithinALongParagraph()
    {
        string paragraph = string.Join(" ", Enumerable.Repeat("Esta oración es lo bastante larga para que el párrafo se parta.", 12));
        var options = new MarkdownSpeechOptions { LongSegmentChars = 80 };
        var v1 = Parse(paragraph, options);
        var v2 = Parse("Nuevo.\n\n" + paragraph, options);
        Assert.True(v1.Segments.Count >= 3);

        var point = ResumePoint.Of(v1, 2);

        Assert.Equal(2, point.Part);
        Assert.Equal(1 + 2, ResumeLocator.Find(v2, point));
    }

    [Fact]
    public void Resume_FallsBackToTheSameIndex_WhenTheParagraphChanged()
    {
        var v1 = Parse("# A\n\nUno.\n\nDos.\n\nTres.");
        var v2 = Parse("# A\n\nUno.\n\nDos.\n\nTres y medio.");

        Assert.Equal(3, ResumeLocator.Find(v2, ResumePoint.Of(v1, 3)));
    }

    [Fact]
    public void Resume_PicksTheNearestCopy_WhenTheParagraphIsRepeated()
    {
        var doc = Parse("# A\n\nIgual.\n\nUno.\n\nDos.\n\nTres.\n\nIgual.");
        Assert.Equal(doc.Segments[1].BlockKey, doc.Segments[5].BlockKey);

        Assert.Equal(5, ResumeLocator.Find(doc, ResumePoint.Of(doc, 5)));
        Assert.Equal(1, ResumeLocator.Find(doc, ResumePoint.Of(doc, 1)));
    }

    [Fact]
    public void Resume_WithoutKey_UsesTheIndex()
    {
        var doc = Parse("# A\n\nUno.\n\nDos.\n\nTres.");

        Assert.Equal(3, ResumeLocator.Find(doc, new ResumePoint(99, null, 0)));
    }

    [Fact]
    public void Resume_OfAnOutOfRangeId_HasNoKey()
    {
        var doc = Parse("Uno.");

        Assert.Equal(new ResumePoint(7, null, 0), ResumePoint.Of(doc, 7));
    }

    [Fact]
    public void Resume_OfAnEmptyDocument_IsMinusOne()
    {
        Assert.Equal(-1, ResumeLocator.Find(Parse(""), new ResumePoint(2, null, 0)));
    }
}
