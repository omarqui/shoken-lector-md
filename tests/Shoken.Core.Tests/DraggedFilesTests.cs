using Shoken.Core.Desktop;

namespace Shoken.Core.Tests;

public class DraggedFilesTests
{
    private static readonly Func<string, bool> AllExist = _ => true;

    [Fact]
    public void Dragged_OneMarkdown_OffersToOpenItByName()
    {
        DraggedFiles dragged = DraggedFiles.Describe([@"C:\docs\guia.md"], AllExist);

        Assert.True(dragged.CanDrop);
        Assert.Equal([@"C:\docs\guia.md"], dragged.Openable);
        Assert.Equal("Suelta para abrir guia.md", dragged.Title);
        Assert.Null(dragged.Detail);
    }

    [Fact]
    public void Dragged_SeveralMarkdown_CountsThem()
    {
        DraggedFiles dragged = DraggedFiles.Describe([@"C:\a.md", @"C:\b.markdown", @"C:\c.txt"], AllExist);

        Assert.Equal(3, dragged.Openable.Count);
        Assert.Equal("Suelta para abrir 3 documentos", dragged.Title);
        Assert.Null(dragged.Detail);
    }

    [Fact]
    public void Dragged_Mixed_SaysHowManyOpen_AndNamesTheOnlyOneLeftOut()
    {
        DraggedFiles dragged = DraggedFiles.Describe([@"C:\a.md", @"C:\b.md", @"C:\fotos\foto.png"], AllExist);

        Assert.Equal([@"C:\a.md", @"C:\b.md"], dragged.Openable);
        Assert.Equal("Suelta para abrir 2 de 3", dragged.Title);
        Assert.Equal("foto.png no es un documento Markdown", dragged.Detail);
    }

    [Fact]
    public void Dragged_MixedWithSeveralLeftOut_SaysTheRestAreNotMarkdown()
    {
        DraggedFiles dragged = DraggedFiles.Describe([@"C:\x.pdf", @"C:\guia.md", @"C:\y.png"], AllExist);

        Assert.Equal([@"C:\guia.md"], dragged.Openable);
        Assert.Equal("Suelta para abrir guia.md", dragged.Title);
        Assert.Equal("Los demás no son documentos Markdown", dragged.Detail);
    }

    [Fact]
    public void Dragged_NothingOpenable_CannotDrop_AndSaysWhatOpens()
    {
        DraggedFiles one = DraggedFiles.Describe([@"C:\guia.pdf"], AllExist);
        DraggedFiles several = DraggedFiles.Describe([@"C:\a.pdf", @"C:\b.png"], AllExist);

        Assert.False(one.CanDrop);
        Assert.Empty(one.Openable);
        Assert.Equal("guia.pdf no es un documento Markdown", one.Title);
        Assert.Equal("Shoken abre archivos .md, .markdown, .mdx, .txt y .docx", one.Detail);
        Assert.Equal("Ninguno es un documento Markdown", several.Title);
        Assert.Equal(one.Detail, several.Detail);
    }

    [Fact]
    public void Dragged_FolderOrMissingFile_IsNotOpenable_EvenWithAMarkdownName()
    {
        DraggedFiles dragged = DraggedFiles.Describe([@"C:\notas.md", @"C:\guia.md"], path => path == @"C:\guia.md");

        Assert.Equal([@"C:\guia.md"], dragged.Openable);
        Assert.Equal("notas.md no es un documento Markdown", dragged.Detail);
    }

    [Fact]
    public void Dragged_WithoutPaths_CannotDrop()
    {
        // Lo que no trae rutas (un adjunto de correo, un archivo dentro de un .zip) no se puede abrir.
        DraggedFiles dragged = DraggedFiles.Describe([], AllExist);

        Assert.False(dragged.CanDrop);
        Assert.Equal("No se puede abrir desde aquí", dragged.Title);
        Assert.Equal("Guárdalo en una carpeta y arrástralo desde ahí", dragged.Detail);
    }

    [Fact]
    public void Openable_AcceptsTheMarkdownExtensions_IgnoringCase()
    {
        Assert.True(DraggedFiles.IsOpenable(@"C:\GUIA.MD"));
        Assert.True(DraggedFiles.IsOpenable(@"C:\a.Markdown"));
        Assert.True(DraggedFiles.IsOpenable(@"C:\a.mdx"));
        Assert.True(DraggedFiles.IsOpenable(@"C:\a.txt"));
        Assert.False(DraggedFiles.IsOpenable(@"C:\a.pdf"));
        Assert.False(DraggedFiles.IsOpenable(@"C:\md"));
    }

    [Fact]
    public void Openable_AcceptsWordDocx()
    {
        Assert.True(DraggedFiles.IsOpenable(@"C:\informe.DOCX"));
        Assert.False(DraggedFiles.IsOpenable(@"C:\viejo.doc"));
    }
}
