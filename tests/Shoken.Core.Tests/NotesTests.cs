using Shoken.Core.Model;
using Shoken.Core.Notes;
using Shoken.Core.Parsing;

namespace Shoken.Core.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shoken-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static DocumentNote Note(string quote, int line, NoteKind kind = NoteKind.Clarify, string text = "nota",
        int? endLine = null, string? section = null, bool resolved = false, bool orphaned = false) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"), Quote = quote, Line = line, EndLine = endLine ?? line, Section = section,
            Kind = kind, Text = text, Resolved = resolved, Orphaned = orphaned,
        };

    // ------------------------------------------------------------------ guardado

    [Fact]
    public void Store_KeepsEachDocumentsNotes_ApartAndIgnoringTheCaseOfThePath()
    {
        var store = new NoteStore(_dir);
        store.Save(@"C:\Docs\guia.md", [Note("una cita", 3, NoteKind.Improve, "mejóralo")]);
        store.Save(@"C:\Docs\otro.md", [Note("otra", 1)]);

        List<DocumentNote> notes = store.Load(@"c:\docs\GUIA.md");

        DocumentNote note = Assert.Single(notes);
        Assert.Equal(("una cita", 3, NoteKind.Improve, "mejóralo"), (note.Quote, note.Line, note.Kind, note.Text));
        Assert.Single(store.Load(@"C:\Docs\otro.md"));
        Assert.Empty(store.Load(@"C:\Docs\sin-apuntes.md"));
    }

    [Fact]
    public void Store_MovesTheNotes_WhenAPastedTextIsSavedAsAFile_AndDeletesThem()
    {
        var store = new NoteStore(_dir);
        store.Save(@"C:\Datos\pegados\abc.md", [Note("cita", 2)]);

        store.Move(@"C:\Datos\pegados\abc.md", @"C:\Docs\guardado.md");

        Assert.Empty(store.Load(@"C:\Datos\pegados\abc.md"));
        Assert.Single(store.Load(@"C:\Docs\guardado.md"));

        store.Delete(@"C:\Docs\guardado.md");
        Assert.Empty(store.Load(@"C:\Docs\guardado.md"));
    }

    [Fact]
    public void Store_WithoutNotes_LeavesNoFile_AndADamagedFileReadsAsEmpty()
    {
        var store = new NoteStore(_dir);
        store.Save(@"C:\Docs\guia.md", [Note("cita", 1)]);
        store.Save(@"C:\Docs\guia.md", []);
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);

        Directory.CreateDirectory(_dir);
        store.Save(@"C:\Docs\roto.md", [Note("cita", 1)]);
        File.WriteAllText(Directory.GetFiles(_dir).Single(), "{ esto no es json");
        Assert.Empty(store.Load(@"C:\Docs\roto.md"));
    }

    [Fact]
    public void Store_DoesNotSaveWhetherANoteIsOrphaned_ItIsRecomputedOnOpen()
    {
        var store = new NoteStore(_dir);
        store.Save(@"C:\Docs\guia.md", [Note("cita", 1, orphaned: true)]);

        Assert.False(store.Load(@"C:\Docs\guia.md").Single().Orphaned);
    }

    [Fact]
    public void Notes_CurrentQuote_IsNotSaved()
    {
        var store = new NoteStore(_dir);
        DocumentNote note = Note("cita", 1);
        note.CurrentQuote = "cita retocada";
        store.Save(@"C:\Docs\guia.md", [note]);

        Assert.Null(store.Load(@"C:\Docs\guia.md").Single().CurrentQuote);
    }

    [Fact]
    public void Exporter_ShowsTheOriginalAndTheCurrentText_WhenTheQuoteChanged()
    {
        DocumentNote note = Note("Si la DGII rechaza el envío, reintenta tres veces.", 3, text: "revisa el límite");
        note.CurrentQuote = "Si la DGII rechaza el envío, reintenta cinco veces.";

        string md = NoteExporter.ToMarkdown(null, new NoteSource(@"C:\Docs\guia.md", "Guía"), [note]);

        Assert.Contains("Cita original:\n\n> Si la DGII rechaza el envío, reintenta tres veces.\n\n" +
            "Ahora dice:\n\n> Si la DGII rechaza el envío, reintenta cinco veces.\n\nrevisa el límite", md);
    }

    [Fact]
    public void Exporter_QuotesOnlyTheOriginal_WhenTheQuoteDidNotChange()
    {
        string md = NoteExporter.ToMarkdown(null, new NoteSource(@"C:\Docs\guia.md", "Guía"), [Note("cita", 3)]);

        Assert.DoesNotContain("Ahora dice:", md);
        Assert.DoesNotContain("Cita original:\n", md);
        Assert.Contains("> cita\n", md);
    }

    [Fact]
    public void Exporter_DefaultInstruction_ExplainsChangedQuotes()
    {
        Assert.Contains("Cita original", NoteExporter.DefaultInstruction);
        Assert.Contains("Ahora dice", NoteExporter.DefaultInstruction);
    }

    [Fact]
    public void Settings_ShowTheNotesColumn_UseTheDefaultInstruction_AndKeepNotesInTheAppData()
    {
        var settings = new Settings.AppSettings();

        Assert.True(settings.ShowNotes);
        Assert.Null(settings.NotesInstruction); // null = la instrucción que trae la app (y sus mejoras futuras)
        Assert.Equal(Path.Combine(_dir, "apuntes"), new Settings.AppPaths(_dir).NotesDirectory);
    }

    // ------------------------------------------------------------------ referencias

    [Fact]
    public void Reference_GivesTheLinesOfTheMarkedBlocks_AndTheSectionPath()
    {
        var doc = new MarkdownSpeechParser().Parse("# Guía\n\nIntro.\n\n## Instalación\n\n### Requisitos\n\nPrimera\nsegunda.\n\nOtra cosa.");
        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(doc);
        string Anchor(string start) => doc.Segments.First(s => s.PlainText.StartsWith(start, StringComparison.Ordinal)).AnchorId;

        NoteLocation location = NoteReferences.Describe(doc, outline, Anchor("Primera"), Anchor("Otra"));

        Assert.Equal(9, location.Line);
        Assert.Equal(12, location.EndLine);
        Assert.Equal("Guía › Instalación › Requisitos", location.Section);
        Assert.Null(NoteReferences.Describe(doc, outline, "no-existe", "no-existe").Section);
    }

    // ------------------------------------------------------------------ exportación

    [Fact]
    public void Export_StartsWithTheInstruction_WithItsPlaceholdersFilled()
    {
        string text = NoteExporter.ToMarkdown("Revisa {archivo} («{titulo}»): {cantidad} apuntes.",
            new NoteSource(@"C:\Repos\docs\guia.md", "Guía"), [Note("a", 1), Note("b", 2)]);

        Assert.StartsWith(@"Revisa C:\Repos\docs\guia.md («Guía»): 2 apuntes.", text);
    }

    [Fact]
    public void Export_ListsPendingNotesInDocumentOrder_WithFileLineReferenceSectionQuoteAndNote()
    {
        DocumentNote second = Note("segunda cita", 40, NoteKind.Improve, "más corto", endLine: 42, section: "Uso");
        DocumentNote first = Note("primera\ncita", 12, NoteKind.Clarify, "¿qué significa?", section: "Guía › Requisitos");
        DocumentNote done = Note("ya resuelta", 5, resolved: true);

        string text = NoteExporter.ToMarkdown(null, new NoteSource(@"C:\Repos\docs\guia.md", "Guía"), [second, done, first]);

        int a = text.IndexOf("## 1. Aclarar · `guia.md:12` · § Guía › Requisitos", StringComparison.Ordinal);
        int b = text.IndexOf("## 2. Mejorar · `guia.md:40-42` · § Uso", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a, text);
        Assert.Contains("> primera\n> cita", text);
        Assert.Contains("¿qué significa?", text);
        Assert.DoesNotContain("ya resuelta", text);
        Assert.Contains(@"C:\Repos\docs\guia.md", text); // la instrucción por omisión trae la ruta completa
    }

    [Fact]
    public void Export_PutsNotesNoLongerInTheDocumentLast_AndPastedTextHasNoLineNumbers()
    {
        string file = NoteExporter.ToMarkdown(null, new NoteSource(@"C:\d\guia.md", "Guía"),
            [Note("se fue", 3, orphaned: true), Note("sigue", 9)]);
        Assert.True(file.IndexOf("sigue", StringComparison.Ordinal) < file.IndexOf("se fue", StringComparison.Ordinal));
        Assert.Contains("Ya no están en el documento", file);

        string pasted = NoteExporter.ToMarkdown(null, new NoteSource(null, "Notas de la reunión"), [Note("cita", 4, section: "Acuerdos")]);
        Assert.Contains("texto pegado «Notas de la reunión»", pasted);
        Assert.Contains("## 1. Aclarar · § Acuerdos", pasted);
        Assert.DoesNotContain(":4", pasted);
    }
}
