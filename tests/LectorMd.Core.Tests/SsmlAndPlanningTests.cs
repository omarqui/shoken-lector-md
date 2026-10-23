using System.Xml.Linq;
using LectorMd.Core.Model;
using LectorMd.Core.Parsing;
using LectorMd.Core.Planning;
using LectorMd.Core.Ssml;
using LectorMd.Core.Text;

namespace LectorMd.Core.Tests;

public class SsmlAndPlanningTests
{
    private static SpeechDocument Parse(string markdown) => new MarkdownSpeechParser().Parse(markdown);

    private static SpeechChunk WholeDocument(SpeechDocument doc) =>
        new() { Index = 0, Segments = doc.Segments };

    [Fact]
    public void Ssml_IsValidXml_WithOneBookmarkPerSegment()
    {
        var doc = Parse("# Título <raro> & \"comillas\"\n\nTexto con **negrita**.\n\n- uno\n- dos");
        var ssml = new SsmlBuilder(new SpeechTextNormalizer()).Build(WholeDocument(doc), "es-MX-DaliaNeural");

        XDocument xml = XDocument.Parse(ssml.Ssml);
        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        var marks = xml.Descendants(ns + "bookmark").Select(b => (string?)b.Attribute("mark")).ToList();

        Assert.Equal(Enumerable.Range(0, doc.Segments.Count).Select(i => i.ToString()), marks);
        Assert.Equal("es-MX-DaliaNeural", (string?)xml.Descendants(ns + "voice").Single().Attribute("name"));
    }

    [Fact]
    public void Ssml_BookmarksAreDirectChildrenOfVoice()
    {
        var doc = Parse("# Título\n\n> Cita con **fuerza**.");
        var ssml = new SsmlBuilder(new SpeechTextNormalizer()).Build(WholeDocument(doc), "v", ratePercent: 10);

        XNamespace ns = "http://www.w3.org/2001/10/synthesis";
        var bookmarks = XDocument.Parse(ssml.Ssml).Descendants(ns + "bookmark").ToList();
        Assert.All(bookmarks, b => Assert.Equal("voice", b.Parent!.Name.LocalName));
    }

    [Fact]
    public void Ssml_AddsBreaksAndHeadingProsody()
    {
        var doc = Parse("Intro.\n\n## Sección\n\nCuerpo.");
        string ssml = new SsmlBuilder(new SpeechTextNormalizer()).Build(WholeDocument(doc), "v").Ssml;

        Assert.Contains("<break time=\"1270ms\"/>", ssml); // 320 después del párrafo + 950 antes del h2
        Assert.Contains("<prosody rate=\"-6%\" pitch=\"-3%\">Sección.</prosody>", ssml);
        Assert.Contains("Muy <prosody rate=\"-4%\" pitch=\"+6%\">claro</prosody>.", new SsmlBuilder(new SpeechTextNormalizer())
            .Build(WholeDocument(Parse("Muy **claro**.")), "v").Ssml);
    }

    [Fact]
    public void Ssml_EmphasisAndQuotesAddNoMarkupByDefault()
    {
        var doc = Parse("> Una *cita* breve.");
        string ssml = new SsmlBuilder(new SpeechTextNormalizer()).Build(WholeDocument(doc), "v").Ssml;

        Assert.DoesNotContain("<prosody", ssml);
        Assert.Contains("Una cita breve.", ssml);
    }

    [Fact]
    public void Ssml_BillableCharacters_ExcludeSpeakAndVoiceTags()
    {
        var doc = Parse("Hola.");
        var ssml = new SsmlBuilder(new SpeechTextNormalizer()).Build(WholeDocument(doc), "es-MX-JorgeNeural");

        Assert.Equal("<bookmark mark=\"0\"/>Hola.".Length, ssml.BillableCharacters);
    }

    [Fact]
    public void CombinePercent_IsMultiplicative()
    {
        Assert.Equal(-6, SsmlBuilder.CombinePercent(0, -6));
        Assert.Equal(3, SsmlBuilder.CombinePercent(10, -6));
    }

    [Fact]
    public void Planner_CoversEverySegmentOnceAndInOrder()
    {
        string md = string.Join("\n\n", Enumerable.Range(1, 60).Select(i =>
            i % 10 == 1 ? $"## Sección {i}" : $"Párrafo número {i} con algo de texto para que pese un poco más."));
        var doc = Parse(md);

        var chunks = ChunkPlanner.Plan(doc.Segments);

        Assert.Equal(doc.Segments.Select(s => s.Id), chunks.SelectMany(c => c.Segments).Select(s => s.Id));
        Assert.True(chunks[0].CharacterCount <= 350 + 80);
        Assert.Equal(0, chunks[0].LeadingPauseMs);
    }

    [Fact]
    public void Planner_StartsNewChunkAtSectionHeadings()
    {
        string body = string.Concat(Enumerable.Repeat("Texto de relleno para la sección. ", 10));
        var doc = Parse($"## Uno\n\n{body}\n\n## Dos\n\n{body}");

        var chunks = ChunkPlanner.Plan(doc.Segments);

        Assert.Contains(chunks, c => c.Segments[0].IsHeading && c.Segments[0].PlainText == "Dos");
        var second = chunks.First(c => c.Segments[0].PlainText == "Dos");
        Assert.True(second.LeadingPauseMs > 0);
    }

    [Fact]
    public void Planner_IsDeterministic()
    {
        var doc = Parse(string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Párrafo {i}.")));

        var a = ChunkPlanner.Plan(doc.Segments).Select(c => (c.FirstSegmentId, c.LastSegmentId));
        var b = ChunkPlanner.Plan(doc.Segments).Select(c => (c.FirstSegmentId, c.LastSegmentId));

        Assert.Equal(a, b);
    }
}
