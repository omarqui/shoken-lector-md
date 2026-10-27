namespace Shoken.Core.Desktop;

/// <summary>
/// Archivos que se arrastran sobre la ventana: cuáles se pueden abrir y qué decir antes de que se suelten.
/// </summary>
public sealed class DraggedFiles
{
    private static readonly string[] OpenableExtensions = [".md", ".markdown", ".mdx", ".txt", ".docx"];

    private DraggedFiles(IReadOnlyList<string> openable, string title, string? detail)
    {
        Openable = openable;
        Title = title;
        Detail = detail;
    }

    /// <summary>Los que se abren al soltar, en el orden en que vienen.</summary>
    public IReadOnlyList<string> Openable { get; }

    public bool CanDrop => Openable.Count > 0;

    /// <summary>"Suelta para abrir guia.md" o, si no se puede, por qué.</summary>
    public string Title { get; }

    /// <summary>Lo que se queda afuera y no se abre, o null si se abre todo.</summary>
    public string? Detail { get; }

    /// <summary>Si la app abre un documento con esa extensión (no mira si el archivo existe).</summary>
    public static bool IsOpenable(string path) =>
        OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Qué pasará al soltar <paramref name="paths"/>. Una carpeta o un archivo que ya no está no se abren aunque su
    /// nombre termine en .md, por eso también se pregunta si existe.
    /// </summary>
    public static DraggedFiles Describe(IReadOnlyList<string> paths, Func<string, bool> fileExists)
    {
        if (paths.Count == 0)
        {
            return new DraggedFiles([], "No se puede abrir desde aquí", "Guárdalo en una carpeta y arrástralo desde ahí");
        }

        var openable = paths.Where(p => IsOpenable(p) && fileExists(p)).ToList();
        var leftOut = paths.Except(openable).ToList();
        string NotMarkdown(string path) => $"{Path.GetFileName(path)} no es Markdown ni Word";

        if (openable.Count == 0)
        {
            string extensions = string.Join(", ", OpenableExtensions[..^1]) + " y " + OpenableExtensions[^1];
            return new DraggedFiles([], leftOut.Count == 1 ? NotMarkdown(leftOut[0]) : "Ninguno es Markdown ni Word",
                $"Shoken abre archivos {extensions}");
        }

        string title = openable.Count == 1 ? $"Suelta para abrir {Path.GetFileName(openable[0])}"
            : leftOut.Count == 0 ? $"Suelta para abrir {openable.Count} documentos"
            : $"Suelta para abrir {openable.Count} de {paths.Count}";
        string? detail = leftOut.Count switch
        {
            0 => null,
            1 => NotMarkdown(leftOut[0]),
            _ => "Los demás no son Markdown ni Word",
        };
        return new DraggedFiles(openable, title, detail);
    }
}
