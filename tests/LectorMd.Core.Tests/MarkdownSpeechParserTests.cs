using LectorMd.Core.Model;
using LectorMd.Core.Parsing;

namespace LectorMd.Core.Tests;

public class MarkdownSpeechParserTests
{
    private static SpeechDocument Parse(string markdown, MarkdownSpeechOptions? options = null) =>
        new MarkdownSpeechParser(options).Parse(markdown, "prueba.md");

    [Fact]
    public void Headings_GetLevelAndLongerPausesThanParagraphs()
    {
        var doc = Parse("# Instalación\n\nPrimero instala Docker.\n\n## Requisitos\n\nWindows 11.");

        Assert.Equal(4, doc.Segments.Count);
        Assert.Equal(SegmentKind.Heading, doc.Segments[0].Kind);
        Assert.Equal(1, doc.Segments[0].HeadingLevel);
        Assert.Equal(2, doc.Segments[2].HeadingLevel);
        Assert.True(doc.Segments[2].PauseBeforeMs > doc.Segments[1].PauseAfterMs);
        Assert.Equal("Instalación", doc.Title);
    }

    [Fact]
    public void Ids_MatchIndexes()
    {
        var doc = Parse("# A\n\nUno.\n\n- dos\n- tres\n\n> cuatro");
        for (int i = 0; i < doc.Segments.Count; i++)
        {
            Assert.Equal(i, doc.Segments[i].Id);
        }
    }

    [Fact]
    public void OrderedList_PrefixesNumbersStartingAtOrderedStart()
    {
        var doc = Parse("3. Abrir\n4. Guardar");

        Assert.Equal("3. Abrir", doc.Segments[0].PlainText);
        Assert.Equal("4. Guardar", doc.Segments[1].PlainText);
        Assert.All(doc.Segments, s => Assert.Equal(SegmentKind.ListItem, s.Kind));
    }

    [Fact]
    public void TaskList_IsReadAsDoneOrPending()
    {
        var doc = Parse("- [x] Compilar\n- [ ] Publicar");

        Assert.Equal("Hecho: Compilar", doc.Segments[0].PlainText);
        Assert.Equal("Pendiente: Publicar", doc.Segments[1].PlainText);
    }

    [Fact]
    public void StrongAndEmphasis_KeepTheirStyle()
    {
        var doc = Parse("Esto es **muy importante** y *sutil*.");
        var runs = doc.Segments[0].Runs;

        Assert.Contains(runs, r => r.Text == "muy importante" && r.Style == RunStyle.Strong);
        Assert.Contains(runs, r => r.Text == "sutil" && r.Style == RunStyle.Emphasis);
        Assert.Equal("Esto es muy importante y sutil.", doc.Segments[0].PlainText);
    }

    [Fact]
    public void Table_AnnouncesColumnsAndReadsRowsAsHeaderValuePairs()
    {
        var doc = Parse("| Motor | Costo |\n|---|---|\n| Azure | Gratis |\n| Piper | Libre |");

        Assert.Equal(SegmentKind.TableHeader, doc.Segments[0].Kind);
        Assert.Equal("Tabla con columnas: Motor, Costo.", doc.Segments[0].PlainText);
        Assert.Equal("Motor: Azure. Costo: Gratis.", doc.Segments[1].PlainText);
        Assert.Equal(SegmentKind.TableRow, doc.Segments[2].Kind);
    }

    [Fact]
    public void Table_CanBeSkipped()
    {
        var doc = Parse("Antes.\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\nDespués.", new MarkdownSpeechOptions { Tables = TableMode.Skip });

        Assert.Equal(2, doc.Segments.Count);
        Assert.True(doc.Segments[1].PauseBeforeMs > 0);
    }

    [Fact]
    public void CodeBlock_IsAnnouncedWithLanguageByDefault()
    {
        var doc = Parse("```csharp\nvar x = 1;\n```");

        Assert.Single(doc.Segments);
        Assert.Equal("Bloque de código en C#.", doc.Segments[0].PlainText);
    }

    [Fact]
    public void CodeBlock_CanBeSkippedOrRead()
    {
        const string md = "Texto.\n\n```bash\ndocker ps\n```";

        var skipped = Parse(md, new MarkdownSpeechOptions { CodeBlocks = CodeBlockMode.Skip });
        Assert.Single(skipped.Segments);

        var read = Parse(md, new MarkdownSpeechOptions { CodeBlocks = CodeBlockMode.Read });
        Assert.Equal("Código en bash: docker ps.", read.Segments[1].PlainText);
    }

    [Fact]
    public void Alert_IsAnnouncedInSpanish()
    {
        var doc = Parse("> [!WARNING]\n> No borres la base de datos.");

        Assert.Equal("Advertencia.", doc.Segments[0].PlainText);
        Assert.Equal(SegmentKind.Quote, doc.Segments[1].Kind);
    }

    [Fact]
    public void FrontMatterHtmlAndImages_AreNotRead()
    {
        var doc = Parse("---\ntitle: X\n---\n\n<div>oculto</div>\n\n![logo](logo.png) Hola.");

        Assert.Single(doc.Segments);
        Assert.Equal("Hola.", doc.Segments[0].PlainText);
    }

    [Fact]
    public void Links_ReadTheirTextAndBareUrlsBecomeEnlace()
    {
        var doc = Parse("Lee [la guía](https://ejemplo.com/guia) o <https://ejemplo.com>.");

        Assert.Equal("Lee la guía o enlace.", doc.Segments[0].PlainText);
    }

    [Fact]
    public void ThematicBreak_AddsPauseToNextSegment()
    {
        var withBreak = Parse("Uno.\n\n---\n\nDos.");
        var withoutBreak = Parse("Uno.\n\nDos.");

        Assert.True(withBreak.Segments[1].PauseBeforeMs > withoutBreak.Segments[1].PauseBeforeMs);
    }

    [Fact]
    public void LongParagraph_IsSplitBySentencesSharingTheSameAnchor()
    {
        string sentence = "Esta es una oración de prueba bastante larga para el lector. ";
        string paragraph = string.Concat(Enumerable.Repeat(sentence, 40));
        var doc = Parse(paragraph, new MarkdownSpeechOptions { LongSegmentChars = 600 });

        Assert.True(doc.Segments.Count > 1);
        Assert.All(doc.Segments, s => Assert.Equal(doc.Segments[0].AnchorId, s.AnchorId));
        Assert.All(doc.Segments, s => Assert.True(s.PlainText.Length <= 700));
        Assert.Equal(paragraph.Trim(), string.Join(" ", doc.Segments.Select(s => s.PlainText)));
    }

    [Fact]
    public void Mermaid_IsReadAsDiagrama_AndStaysInPreMermaidWithItsAnchorForThePageToDraw()
    {
        // lector.js dibuja cada <pre class="mermaid"> con Mermaid; el data-seg sigue marcando lo que se lee.
        var doc = Parse("```mermaid\nsequenceDiagram\n  A->>B: Hola <b>x</b>\n```");

        SpeechSegment diagram = Assert.Single(doc.Segments);
        Assert.Equal("Diagrama.", diagram.PlainText);
        Assert.Contains($"<pre class=\"mermaid\" data-seg=\"{diagram.AnchorId}\" data-line=\"1\">sequenceDiagram", doc.HtmlBody);
        Assert.Contains("A->>B: Hola &lt;b>x&lt;/b>", doc.HtmlBody); // escapado: el código no se vuelve HTML
    }

    [Fact]
    public void Segments_KnowWhereTheyStartAndEndInTheMarkdown_AndTheHtmlCarriesTheStartLine()
    {
        var doc = Parse("# Título\n\nPrimera línea\nsegunda línea\ntercera.\n\n- uno\n- dos");

        SpeechSegment heading = doc.Segments[0];
        SpeechSegment paragraph = doc.Segments.Single(s => s.PlainText.StartsWith("Primera", StringComparison.Ordinal));
        SpeechSegment second = doc.Segments.Single(s => s.PlainText.Contains("dos", StringComparison.Ordinal));

        Assert.Equal((1, 1), (heading.Line, heading.EndLine));
        Assert.Equal((3, 5), (paragraph.Line, paragraph.EndLine));
        Assert.Equal((8, 8), (second.Line, second.EndLine));
        Assert.Contains($"data-seg=\"{paragraph.AnchorId}\" data-line=\"3\"", doc.HtmlBody);
    }

    [Fact]
    public void Html_ContainsAnAnchorForEverySegment()
    {
        var doc = Parse("# T\n\nPárrafo.\n\n- uno\n- dos\n\n| A |\n|---|\n| 1 |\n\n```js\nx\n```\n\n> cita");

        foreach (var anchor in doc.Segments.Select(s => s.AnchorId).Distinct())
        {
            Assert.Contains($"data-seg=\"{anchor}\"", doc.HtmlBody);
        }
    }
}
