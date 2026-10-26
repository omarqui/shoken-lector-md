using System.Xml.Linq;
using Shoken.Core.Audio;
using Shoken.Core.Demo;
using Shoken.Core.Model;
using Shoken.Core.Planning;
using Shoken.Core.Ssml;

namespace Shoken.Core.Tests;

public class DemoTests
{
    private static readonly XNamespace Ssml = "http://www.w3.org/2001/10/synthesis";

    [Fact]
    public void Demo_RecordingMatchesCurrentSsml()
    {
        const string Rerecord = "La grabación de la demo quedó vieja (cambió el texto o cómo se arma el SSML). " +
                                "Vuelve a grabarla con: dotnet run empaquetado/generar-demo.cs";
        DemoRecording recording = IntonationDemo.Recording ?? throw new Xunit.Sdk.XunitException(Rerecord);
        DemoPlan plan = IntonationDemo.RecordedPlan();

        var current = IntonationDemo.RecordedSsml(plan).Select(d => IntonationDemo.Fingerprint(d.Ssml)).ToList();
        string flat = IntonationDemo.Fingerprint(IntonationDemo.FlatSsml(plan, IntonationDemo.RecordingVoice).Ssml);

        Assert.True(current.SequenceEqual(recording.Con.Fingerprints) && recording.Sin.Fingerprints.SequenceEqual([flat]), Rerecord);
        Assert.Equal(IntonationDemo.RecordingVoice, recording.Voice);
        Assert.Equal(plan.Document.Segments.Count, recording.Con.Marks.Count);
        Assert.Equal(plan.Document.Segments.Count, recording.Sin.Marks.Count);
    }

    [Fact]
    public void Demo_RecordingMarksIncreaseInsideTheAudio()
    {
        DemoRecording recording = IntonationDemo.Recording!;
        foreach (DemoTake take in new[] { recording.Con, recording.Sin })
        {
            Assert.Equal(0, take.Marks[0]);
            Assert.True(take.Marks.Zip(take.Marks.Skip(1)).All(p => p.First < p.Second), $"{take.File}: marcas fuera de orden");
            Assert.True(take.Marks[^1] < take.DurationSeconds);
        }

        // La entonación alarga la lectura: pausas y frases cerradas.
        Assert.True(recording.Con.DurationSeconds > recording.Sin.DurationSeconds);
    }

    [Fact]
    public void Demo_DescribeExplainsPausesProsodyAndAddedPeriod()
    {
        IReadOnlyList<DemoSegment> segments = IntonationDemo.Describe(IntonationDemo.RecordedPlan());

        DemoSegment title = segments[0];
        Assert.Equal(SegmentKind.Heading, title.Kind);
        Assert.Equal(new ProsodyChange(-6, -3), title.Prosody);
        Assert.True(title.AddsPeriod);
        Assert.Equal(0, title.PauseBeforeMs);

        DemoSegment intro = segments[1];
        Assert.Equal(["formato"], intro.Strong);
        Assert.Equal(new ProsodyChange(-4, 6), intro.StrongProsody);
        Assert.False(intro.AddsPeriod); // ya termina en punto
        Assert.True(intro.PauseBeforeMs > 0);

        Assert.All(segments.Where(s => s.Kind == SegmentKind.ListItem), s => Assert.True(s.AddsPeriod));
        Assert.Equal(2, segments.Count(s => s.ReadsNumber));
        Assert.Equal("Consejo", Assert.Single(segments, s => s.Announces is not null).Announces);

        // «Consejo», entre comillas, sube el tono como en el SSML.
        DemoSegment named = Assert.Single(segments, s => s.Quoted.Count > 0);
        Assert.Equal(["«Consejo»"], named.Quoted);
        Assert.Equal(new ProsodyChange(0, 8), named.QuotedProsody);

        // La pausa más larga es la de la línea horizontal (cambio de tema).
        Assert.Equal(segments[^1].PauseBeforeMs, segments.Max(s => s.PauseBeforeMs));
    }

    [Fact]
    public void Demo_DescribeTakesPausesFromThePlan_IncludingTheLeadingPauseOfEachChunk()
    {
        DemoPlan plan = IntonationDemo.RecordedPlan();
        IReadOnlyList<DemoSegment> segments = IntonationDemo.Describe(plan);

        foreach (SpeechChunk chunk in plan.Chunks.Skip(1))
        {
            Assert.Equal(chunk.LeadingPauseMs, segments[chunk.FirstSegmentId].PauseBeforeMs);
        }
    }

    [Fact]
    public void Demo_FlatChunkIsOneUnstyledParagraphWithEveryWord()
    {
        DemoPlan plan = IntonationDemo.RecordedPlan();
        SpeechChunk flat = IntonationDemo.FlatChunk(plan.Document);

        SpeechSegment only = Assert.Single(flat.Segments);
        Assert.Equal(SegmentKind.Paragraph, only.Kind);
        Assert.Equal(0, only.PauseBeforeMs + only.PauseAfterMs);
        Assert.All(only.Runs, r => Assert.Equal(RunStyle.None, r.Style));
        Assert.All(plan.Document.Segments, s => Assert.Contains(s.PlainText.Trim(), only.PlainText));

        // Con el SsmlBuilder de la app: sin pausas ni prosodia.
        string ssml = new SsmlBuilder(plan.Normalizer).Build(flat, "v").Ssml;
        Assert.DoesNotContain("<break", ssml);
        Assert.DoesNotContain("<prosody", ssml);
    }

    [Fact]
    public void Demo_FlatSsmlHasNoPausesNorProsody_AndOneBookmarkPerSegment()
    {
        DemoPlan plan = IntonationDemo.RecordedPlan();
        SsmlDocument flat = IntonationDemo.FlatSsml(plan, "es-MX-DaliaNeural");

        XDocument xml = XDocument.Parse(flat.Ssml);
        Assert.Empty(xml.Descendants(Ssml + "break"));
        Assert.Empty(xml.Descendants(Ssml + "prosody"));
        var marks = xml.Descendants(Ssml + "bookmark").Select(b => (string?)b.Attribute("mark")).ToList();
        Assert.Equal(Enumerable.Range(0, plan.Document.Segments.Count).Select(i => i.ToString()), marks);
        Assert.All(xml.Descendants(Ssml + "bookmark"), b => Assert.Equal("voice", b.Parent!.Name.LocalName));

        // Sin el punto que agrega Shoken: el título queda pegado a lo que sigue.
        Assert.Contains("Los títulos <bookmark", flat.Ssml);
    }

    [Fact]
    public void Demo_MeasureCostSplitsTextBookmarksPausesAndProsody()
    {
        var document = new SsmlDocument(
            "<speak><voice name=\"v\"><bookmark mark=\"0\"/><prosody rate=\"-6%\">Hola.</prosody> <break time=\"650ms\"/><bookmark mark=\"1\"/>Adiós.</voice></speak>",
            "<bookmark mark=\"0\"/><prosody rate=\"-6%\">Hola.</prosody> <break time=\"650ms\"/><bookmark mark=\"1\"/>Adiós.".Length);

        SsmlCost cost = IntonationDemo.MeasureCost([document]);

        Assert.Equal(40, cost.Bookmarks);
        Assert.Equal("<break time=\"650ms\"/>".Length, cost.Pauses);
        Assert.Equal("<prosody rate=\"-6%\">".Length + "</prosody>".Length, cost.Prosody);
        Assert.Equal("Hola. Adiós.".Length, cost.Text);
    }

    [Fact]
    public void Demo_EstimatedStartsAreProportionalAndIncreasing()
    {
        SpeechDocument document = IntonationDemo.RecordedPlan().Document;
        IReadOnlyList<TimeSpan> starts = IntonationDemo.EstimateStarts(document, TimeSpan.FromSeconds(48));

        Assert.Equal(document.Segments.Count, starts.Count);
        Assert.Equal(TimeSpan.Zero, starts[0]);
        Assert.True(starts.Zip(starts.Skip(1)).All(p => p.First < p.Second));
        Assert.True(starts[^1] < TimeSpan.FromSeconds(48));
    }

    // Cuadros MPEG 2 Layer III, 24 kHz, 96 kbps (como los de Azure): 288 bytes y 576 muestras (24 ms) cada uno.
    private static byte[] Frames(int count)
    {
        var data = new byte[count * 288];
        for (int i = 0; i < count; i++)
        {
            data[i * 288] = 0xFF;
            data[i * 288 + 1] = 0xF3;
            data[i * 288 + 2] = 0xA4;
            data[i * 288 + 3] = 0xC4;
        }

        return data;
    }

    private static byte[] Id3(int size) =>
        [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, (byte)(size >> 7), (byte)(size & 0x7F), .. new byte[size]];

    [Fact]
    public void Mp3_MeasuresDurationFromItsFrames_SkippingId3()
    {
        Assert.Equal(TimeSpan.FromSeconds(2.4), Mp3Audio.Duration(Frames(100)));
        Assert.Equal(TimeSpan.FromSeconds(2.4), Mp3Audio.Duration([.. Id3(300), .. Frames(100)]));
        Assert.Equal(TimeSpan.Zero, Mp3Audio.Duration([1, 2, 3, 4, 5]));
    }

    [Fact]
    public void Mp3_ConcatenatesChunksWithoutTheirId3Tags()
    {
        byte[] joined = Mp3Audio.Concatenate([[.. Id3(20), .. Frames(10)], Frames(5)]);

        Assert.Equal(15 * 288, joined.Length);
        Assert.Equal(TimeSpan.FromSeconds(0.36), Mp3Audio.Duration(joined));
    }
}
