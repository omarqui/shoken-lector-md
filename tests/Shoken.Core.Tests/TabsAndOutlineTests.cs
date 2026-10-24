using Shoken.Core.Model;
using Shoken.Core.Parsing;
using Shoken.Core.Settings;
using Shoken.Core.Text;

namespace Shoken.Core.Tests;

public class TabsAndOutlineTests
{
    // ------------------------------------------------------------------ rutas pegadas

    [Fact]
    public void PastedPaths_AcceptsQuotesFileUrisAndSeveralLines()
    {
        string text = "\"C:\\Docs\\uno.md\"\r\n  file:///C:/Docs/dos%20b.md  \n\n'C:\\Docs\\tres.md'";

        Assert.Equal([@"C:\Docs\uno.md", @"C:\Docs\dos b.md", @"C:\Docs\tres.md"], PastedPaths.Parse(text));
    }

    [Fact]
    public void PastedPaths_IgnoresTextThatIsNotAnAbsolutePath()
    {
        Assert.Empty(PastedPaths.Parse("hola mundo"));
        Assert.Empty(PastedPaths.Parse(@"docs\relativo.md"));
        Assert.Empty(PastedPaths.Parse("https://ejemplo.com/a.md"));
        Assert.Empty(PastedPaths.Parse(""));
    }

    [Fact]
    public void PastedPaths_MarkdownThatMentionsAPath_IsNotAPathList()
    {
        Assert.Empty(PastedPaths.Parse("# Notas\n\nC:\\Docs\\uno.md"));
    }

    [Fact]
    public void PastedPaths_ExpandsEnvironmentVariables()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "notas.md");

        Assert.Equal([expected], PastedPaths.Parse(@"%USERPROFILE%\notas.md"));
    }

    [Fact]
    public void PastedPaths_RemovesDuplicates()
    {
        Assert.Single(PastedPaths.Parse("C:\\a.md\nc:\\A.md"));
    }

    // ------------------------------------------------------------------ pestañas guardadas

    [Fact]
    public void TabSession_SkipsMissingFilesAndKeepsTheActiveOne()
    {
        var settings = new AppSettings
        {
            OpenTabs =
            [
                new OpenTab { Path = @"C:\a.md" },
                new OpenTab { Path = @"C:\falta.md" },
                new OpenTab { Path = @"C:\c.md", ResumeSegmentId = 7, ScrollY = 120 },
            ],
            ActiveTab = 2,
        };

        RestoredTabs restored = TabSession.Restore(settings, path => path != @"C:\falta.md");

        Assert.Equal([@"C:\a.md", @"C:\c.md"], restored.Tabs.Select(t => t.Path));
        Assert.Equal(1, restored.ActiveIndex);
        Assert.Equal(7, restored.Tabs[1].ResumeSegmentId);
        Assert.Equal(120, restored.Tabs[1].ScrollY);
        Assert.Equal([@"C:\falta.md"], restored.Missing);
    }

    [Fact]
    public void TabSession_WhenTheActiveTabIsMissing_SelectsTheOneThatTookItsPlace()
    {
        var settings = new AppSettings
        {
            OpenTabs = [new OpenTab { Path = "a" }, new OpenTab { Path = "b" }, new OpenTab { Path = "c" }],
            ActiveTab = 1,
        };

        RestoredTabs restored = TabSession.Restore(settings, path => path != "b");

        Assert.Equal(1, restored.ActiveIndex);
        Assert.Equal("c", restored.Tabs[restored.ActiveIndex].Path);
    }

    [Fact]
    public void TabSession_MigratesTheLastFileOfTheVersionWithoutTabs()
    {
        var settings = new AppSettings { LastFile = @"C:\viejo.md" };

        RestoredTabs restored = TabSession.Restore(settings, _ => true);

        Assert.Equal([@"C:\viejo.md"], restored.Tabs.Select(t => t.Path));
        Assert.Equal(0, restored.ActiveIndex);
    }

    [Fact]
    public void TabSession_DropsEmptyTabsAndLostPastesWithoutReportingThem()
    {
        var settings = new AppSettings
        {
            OpenTabs =
            [
                new OpenTab(),
                new OpenTab { Path = @"C:\pegados\x.md", IsPasted = true, Title = "Notas" },
                new OpenTab { Path = @"C:\a.md" },
            ],
        };

        RestoredTabs restored = TabSession.Restore(settings, path => path == @"C:\a.md");

        Assert.Equal([@"C:\a.md"], restored.Tabs.Select(t => t.Path));
        Assert.Empty(restored.Missing);
    }

    [Fact]
    public void OpenTab_NameComesFromTheFileOrThePastedTitle()
    {
        Assert.Equal("guia", new OpenTab { Path = @"C:\docs\guia.md" }.Name);
        Assert.Equal("Notas", new OpenTab { Path = @"C:\pegados\x.md", IsPasted = true, Title = "Notas" }.Name);
        Assert.Equal("Nueva pestaña", new OpenTab().Name);
    }

    [Fact]
    public void TabSession_IsEmptyWhenNothingWasOpen()
    {
        RestoredTabs restored = TabSession.Restore(new AppSettings(), _ => true);

        Assert.Empty(restored.Tabs);
        Assert.Equal(-1, restored.ActiveIndex);
    }

    [Fact]
    public void AppSettings_CloneCopiesTheTabs()
    {
        var settings = new AppSettings { OpenTabs = [new OpenTab { Path = "a", ScrollY = 5 }] };

        AppSettings clone = settings.Clone();
        clone.OpenTabs[0].ScrollY = 99;
        clone.OpenTabs.Add(new OpenTab { Path = "b" });

        Assert.Single(settings.OpenTabs);
        Assert.Equal(5, settings.OpenTabs[0].ScrollY);
    }

    // ------------------------------------------------------------------ índice

    private static SpeechDocument Parse(string markdown) => new MarkdownSpeechParser().Parse(markdown);

    [Fact]
    public void Outline_ListsHeadingsUpToLevelThree_IndentedFromTheShallowest()
    {
        var doc = Parse("## Uno\n\nTexto.\n\n### Uno punto uno\n\n#### Muy hondo\n\n## Dos\n\nMás texto.");

        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(doc);

        Assert.Equal(["Uno", "Uno punto uno", "Dos"], outline.Select(e => e.Text));
        Assert.Equal([0, 1, 0], outline.Select(e => e.Indent));
        Assert.All(outline, e => Assert.True(doc.Segments[e.SegmentId].IsHeading));
    }

    [Fact]
    public void Outline_CurrentEntryIsTheLastHeadingAtOrBeforeTheSegment()
    {
        var doc = Parse("Intro.\n\n# A\n\nTexto a.\n\n# B\n\nTexto b.");
        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(doc);

        int intro = 0;
        int textA = doc.Segments.Single(s => s.PlainText.StartsWith("Texto a", StringComparison.Ordinal)).Id;
        int textB = doc.Segments.Single(s => s.PlainText.StartsWith("Texto b", StringComparison.Ordinal)).Id;

        Assert.Equal(-1, DocumentOutline.IndexOf(outline, intro));
        Assert.Equal(0, DocumentOutline.IndexOf(outline, textA));
        Assert.Equal(1, DocumentOutline.IndexOf(outline, textB));
    }

    [Fact]
    public void Outline_IsEmptyWithoutHeadings()
    {
        Assert.Empty(DocumentOutline.Build(Parse("Solo un párrafo.")));
    }

    // A > A1 > A1a, A > A2, B > B1
    private static IReadOnlyList<OutlineEntry> Tree() =>
        DocumentOutline.Build(Parse("# A\n\nx\n\n## A1\n\nx\n\n### A1a\n\nx\n\n## A2\n\nx\n\n# B\n\nx\n\n## B1\n\nx"));

    private static HashSet<string> Collapsed(IReadOnlyList<OutlineEntry> outline, params string[] titles) =>
        outline.Where(e => titles.Contains(e.Text)).Select(e => e.AnchorId).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Outline_KnowsWhichHeadingsHaveSubheadingsAndWhereEachSectionEnds()
    {
        IReadOnlyList<OutlineEntry> outline = Tree();

        Assert.Equal([true, true, false, false, true, false], outline.Select((_, i) => DocumentOutline.HasChildren(outline, i)));
        Assert.Equal([4, 3, 3, 4, 6, 6], outline.Select((_, i) => DocumentOutline.SectionEnd(outline, i)));
    }

    [Fact]
    public void Outline_CollapsingAHeadingHidesOnlyItsSubheadings()
    {
        IReadOnlyList<OutlineEntry> outline = Tree();

        Assert.Equal([0, 1, 2, 3, 4, 5], DocumentOutline.VisibleRows(outline, Collapsed(outline)));
        Assert.Equal([0, 1, 3, 4, 5], DocumentOutline.VisibleRows(outline, Collapsed(outline, "A1")));
        Assert.Equal([0, 4, 5], DocumentOutline.VisibleRows(outline, Collapsed(outline, "A")));
        Assert.Equal([0, 4], DocumentOutline.VisibleRows(outline, Collapsed(outline, "A", "A1", "B")));
    }

    [Fact]
    public void Outline_AHiddenSectionIsShownByItsCollapsedAncestorsRow()
    {
        IReadOnlyList<OutlineEntry> outline = Tree();
        IReadOnlyList<int> rows = DocumentOutline.VisibleRows(outline, Collapsed(outline, "A"));

        Assert.Equal(0, DocumentOutline.RowOf(rows, 0));
        Assert.Equal(0, DocumentOutline.RowOf(rows, 2)); // A1a, dentro de A contraído
        Assert.Equal(1, DocumentOutline.RowOf(rows, 4));
        Assert.Equal(2, DocumentOutline.RowOf(rows, 5));
        Assert.Equal(-1, DocumentOutline.RowOf(rows, -1)); // antes del primer título
    }

    [Fact]
    public void Outline_FoldFirstLevelShowsTopLevelWithItsSections_NoneShowsEverything()
    {
        IReadOnlyList<OutlineEntry> outline = Tree();

        Assert.Equal([0, 1, 3, 4, 5], DocumentOutline.VisibleRows(outline, DocumentOutline.Folded(outline, OutlineFold.FirstLevel)));
        Assert.Empty(DocumentOutline.Folded(outline, OutlineFold.None));
    }

    [Fact]
    public void Outline_FoldButtonAlternatesFirstLevelAndExpanded_AndGoesToFirstLevelFromAHandMadeState()
    {
        IReadOnlyList<OutlineEntry> outline = Tree();

        Assert.Equal(OutlineFold.FirstLevel, DocumentOutline.NextFold(outline, Collapsed(outline)));
        Assert.Equal(OutlineFold.None, DocumentOutline.NextFold(outline, DocumentOutline.Folded(outline, OutlineFold.FirstLevel)));
        Assert.Equal(OutlineFold.FirstLevel, DocumentOutline.NextFold(outline, Collapsed(outline, "B"))); // contraído a mano
        Assert.Equal(OutlineFold.FirstLevel, DocumentOutline.NextFold(outline, Collapsed(outline, "A", "A1", "B"))); // todo, a mano
    }

    [Fact]
    public void Outline_WithTwoLevelsFirstLevelIsExpanded_SoTheButtonOnlyUndoesHandMadeFolds()
    {
        // Dos niveles: contraer al primer nivel no oculta nada; el botón solo sirve para deshacer lo contraído a mano.
        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(Parse("# A\n\nx\n\n## A1\n\nx\n\n# B\n\nx"));

        Assert.Empty(DocumentOutline.Folded(outline, OutlineFold.FirstLevel));
        Assert.Empty(DocumentOutline.Folded(outline, DocumentOutline.NextFold(outline, Collapsed(outline, "A"))));
    }

    [Theory]
    [InlineData("Propuesta: gestión de archivos", "Propuesta gestión de archivos.md")]
    [InlineData("  ¿Qué es <esto>?  ", "¿Qué es esto.md")]
    [InlineData("", "Pegado.md")]
    [InlineData("***", "Pegado.md")]
    public void PastedTab_SuggestsAFileNameFromItsTitle(string title, string expected)
    {
        Assert.Equal(expected, OpenTab.SuggestFileName(title));
    }

    [Fact]
    public void Outline_CountsSearchMatchesPerSection_IncludingTheHeadingAndSkippingTheIntro()
    {
        var doc = Parse("Intro.\n\n# A\n\nTexto a.\n\nOtro a.\n\n# B\n\nTexto b.");
        IReadOnlyList<OutlineEntry> outline = DocumentOutline.Build(doc);
        string Anchor(string start) => doc.Segments.First(s => s.PlainText.StartsWith(start, StringComparison.Ordinal)).AnchorId;

        var matchesByAnchor = new Dictionary<string, int>
        {
            [Anchor("Intro")] = 4, // antes del primer título: no tiene fila en el índice
            [Anchor("A")] = 1,
            [Anchor("Texto a")] = 2,
            [Anchor("Otro a")] = 1,
            [Anchor("Texto b")] = 3,
            ["s-que-no-existe"] = 9,
        };

        Assert.Equal([4, 3], DocumentOutline.CountMatches(outline, doc, matchesByAnchor));
    }
}
