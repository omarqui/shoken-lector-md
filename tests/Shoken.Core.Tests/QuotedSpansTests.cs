using Shoken.Core.Model;
using Shoken.Core.Parsing;
using Shoken.Core.Planning;
using Shoken.Core.Ssml;
using Shoken.Core.Text;

namespace Shoken.Core.Tests;

public class QuotedSpansTests
{
    private static IReadOnlyList<SpeechRun> Runs(string markdown) =>
        new MarkdownSpeechParser().Parse(markdown).Segments.Single().Runs;

    private static string Styled(string markdown, RunStyle style) =>
        string.Join("|", Runs(markdown).Where(r => r.Style.HasFlag(style)).Select(r => r.Text));

    private static string Ssml(string markdown, int ratePercent = 0)
    {
        SpeechDocument doc = new MarkdownSpeechParser().Parse(markdown);
        return new SsmlBuilder(new SpeechTextNormalizer())
            .Build(new SpeechChunk { Index = 0, Segments = doc.Segments }, "v", ratePercent).Ssml;
    }

    [Theory]
    [InlineData("Pulsa «Guardar» y sigue.", "«Guardar»")]
    [InlineData("Pulsa “Guardar” y sigue.", "“Guardar”")]
    [InlineData("Pulsa \"Guardar\" y sigue.", "\"Guardar\"")]
    [InlineData("Elige \"Usar\", luego \"Cerrar\".", "\"Usar\"|\"Cerrar\"")]
    [InlineData("La versión \"2\" ya salió.", "\"2\"")]
    public void QuotedSpans_MarksEachKindOfQuote(string markdown, string expected) =>
        Assert.Equal(expected, Styled(markdown, RunStyle.Quoted));

    [Fact]
    public void QuotedSpans_MarksAsides_ButNotEnumerators()
    {
        Assert.Equal("(se guarda solo)", Styled("El archivo (se guarda solo) queda listo.", RunStyle.Aside));
        Assert.Equal("", Styled("Primero (1), luego (a) y después (iv).", RunStyle.Aside));
    }

    [Theory]
    [InlineData("Una comilla «sin cerrar sigue.")]
    [InlineData("Un paréntesis (sin cerrar sigue.")]
    [InlineData("La pantalla de 5\" y la de 7\" son chicas.")]
    [InlineData("Vacíos: «» y ().")]
    public void QuotedSpans_IgnoresUnpairedAndInches(string markdown) =>
        Assert.DoesNotContain(Runs(markdown), r => (r.Style & (RunStyle.Quoted | RunStyle.Aside)) != 0);

    [Fact]
    public void QuotedSpans_InnerSpanWins()
    {
        IReadOnlyList<SpeechRun> runs = Runs("Luego (pulsa «Guardar» abajo) y listo.");
        Assert.Equal("«Guardar»", string.Join("|", runs.Where(r => r.Style.HasFlag(RunStyle.Quoted)).Select(r => r.Text)));
        Assert.Equal("(pulsa | abajo)", string.Join("|", runs.Where(r => r.Style.HasFlag(RunStyle.Aside)).Select(r => r.Text)));
        Assert.DoesNotContain(runs, r => r.Style.HasFlag(RunStyle.Quoted) && r.Style.HasFlag(RunStyle.Aside));
    }

    [Fact]
    public void QuotedSpans_CrossesStyles_AndSkipsInlineCode()
    {
        Assert.Equal("«Abre |la ventana|»", Styled("Dice «Abre *la ventana*» y ya.", RunStyle.Quoted));
        Assert.Equal("", Styled("Escribe `print(\"hola\")` y ya.", RunStyle.Quoted));
        Assert.Equal("«|--force|»", Styled("Usa «`--force`» con cuidado.", RunStyle.Quoted));
    }

    [Fact]
    public void QuotedSpans_KeepsTheText()
    {
        const string markdown = "Pulsa «Guardar» (abajo) y luego \"Usar\" en **«Opciones»**.";
        var marked = new MarkdownSpeechParser().Parse(markdown).Segments.Single();
        var plain = new MarkdownSpeechParser { MarkSpans = false }.Parse(markdown).Segments.Single();

        Assert.Equal(plain.PlainText, marked.PlainText);
        Assert.Equal(plain.Runs, QuotedSpans.Unmark(marked.Runs).ToList());
    }

    [Fact]
    public void QuotedSpans_ExampleDocument_KeepsSegmentsAndTextForLocalEngines()
    {
        string markdown = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "ejemplo.md"));
        var marked = new MarkdownSpeechParser().Parse(markdown).Segments;
        var plain = new MarkdownSpeechParser { MarkSpans = false }.Parse(markdown).Segments;

        Assert.Equal(plain.Count, marked.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Runs, QuotedSpans.Unmark(marked[i].Runs).ToList());
        }
    }

    [Fact]
    public void Ssml_WithoutQuotesOrAsides_IsUnchanged()
    {
        const string markdown = "Texto con **negrita**, *cursiva* y `código`.\n\n- uno\n- dos";
        SpeechDocument marked = new MarkdownSpeechParser().Parse(markdown);
        SpeechDocument plain = new MarkdownSpeechParser { MarkSpans = false }.Parse(markdown);
        var builder = new SsmlBuilder(new SpeechTextNormalizer());

        Assert.Equal(
            builder.Build(new SpeechChunk { Index = 0, Segments = plain.Segments }, "v").Ssml,
            builder.Build(new SpeechChunk { Index = 0, Segments = marked.Segments }, "v").Ssml);
    }

    [Fact]
    public void Ssml_QuotesRaisePitch_AndAsidesLowerIt()
    {
        string ssml = Ssml("Pulsa «Guardar» (está abajo) y listo.");
        Assert.Contains("<prosody pitch=\"+8%\">«Guardar»</prosody>", ssml);
        Assert.Contains("<prosody rate=\"+6%\" pitch=\"-6%\">(está abajo)</prosody>", ssml);
    }

    [Fact]
    public void Ssml_BoldInsideQuotes_DoesNotTagLoneQuoteMarks()
    {
        string ssml = Ssml("Pulsa «**Guardar**» ya.");
        Assert.Contains("«<prosody rate=\"-4%\" pitch=\"+6%\">Guardar</prosody>»", ssml);
        Assert.DoesNotContain("pitch=\"+8%\"", ssml);
    }

    [Fact]
    public void Ssml_QuoteInsideAside_ClosesTheAsideAroundIt()
    {
        string ssml = Ssml("Luego (pulsa «Guardar» abajo) y listo.");
        Assert.Contains(
            "<prosody rate=\"+6%\" pitch=\"-6%\">(pulsa</prosody> <prosody pitch=\"+8%\">«Guardar»</prosody> <prosody rate=\"+6%\" pitch=\"-6%\">abajo)</prosody>",
            ssml);
    }

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AGENTS.md")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new DirectoryNotFoundException("No se encontró la raíz del repositorio.");
    }
}
