using System.Globalization;
using System.Text;

namespace LectorMd.Core.Notes;

/// <summary>De qué documento son los apuntes. Sin ruta, es un texto pegado: la IA no tiene el archivo.</summary>
public sealed record NoteSource(string? FilePath, string Title);

/// <summary>
/// Arma el texto en Markdown que se pega en el chat de una IA: la instrucción (plantilla editable en
/// Configuración) y cada apunte pendiente con su referencia (archivo:líneas), sección, cita y nota.
/// </summary>
public static class NoteExporter
{
    public const string DefaultInstruction =
        "Te paso {cantidad} apuntes sobre el documento {archivo} («{titulo}»). Cada apunte dice dónde está " +
        "(archivo:líneas y sección), el texto que señalé y mi nota.\n\n" +
        "- **Aclarar**: explícame ese punto con más claridad y, si el texto lo necesita, propón cómo reescribirlo para que se entienda.\n" +
        "- **Mejorar**: propón una versión mejorada de ese texto (más clara, precisa o completa) sin cambiar lo que quiere decir.\n" +
        "- **Otro**: haz lo que pide la nota.\n\n" +
        "Responde apunte por apunte, en el mismo orden y con su número.";

    public static string KindName(NoteKind kind) => kind switch
    {
        NoteKind.Improve => "Mejorar",
        NoteKind.Other => "Otro",
        _ => "Aclarar",
    };

    /// <param name="instruction">La plantilla de Configuración; null o vacía = <see cref="DefaultInstruction"/>.</param>
    public static string ToMarkdown(string? instruction, NoteSource source, IReadOnlyList<DocumentNote> notes)
    {
        // Pendientes, en el orden en que aparecen; los que ya no están en el documento, al final.
        List<DocumentNote> pending = notes.Where(n => !n.Resolved)
            .OrderBy(n => n.Orphaned)
            .ThenBy(n => n.Line)
            .ThenBy(n => n.CreatedUtc)
            .ToList();

        string where = source.FilePath ?? $"texto pegado «{source.Title}»";
        string header = (string.IsNullOrWhiteSpace(instruction) ? DefaultInstruction : instruction)
            .Replace("{archivo}", where, StringComparison.Ordinal)
            .Replace("{titulo}", source.Title, StringComparison.Ordinal)
            .Replace("{cantidad}", pending.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var text = new StringBuilder(header.TrimEnd()).Append("\n\n");
        text.Append(source.FilePath is null ? $"Documento: texto pegado «{source.Title}»" : $"Documento: `{source.FilePath}`").Append("\n\n");

        bool orphanHeading = false;
        for (int i = 0; i < pending.Count; i++)
        {
            DocumentNote note = pending[i];
            if (note.Orphaned && !orphanHeading)
            {
                text.Append("## Ya no están en el documento\n\n")
                    .Append("Estos apuntes señalan texto que ya no está (quizá ya se corrigió); van con su línea anterior.\n\n");
                orphanHeading = true;
            }

            text.Append("## ").Append(i + 1).Append(". ").Append(KindName(note.Kind));
            if (source.FilePath is not null && note.Line > 0)
            {
                string lines = note.EndLine > note.Line ? $"{note.Line}-{note.EndLine}" : note.Line.ToString(CultureInfo.InvariantCulture);
                text.Append(" · `").Append(Path.GetFileName(source.FilePath)).Append(':').Append(lines).Append('`');
            }

            if (note.Section is { Length: > 0 } section)
            {
                text.Append(" · § ").Append(section);
            }

            text.Append("\n\n");
            foreach (string line in note.Quote.Trim().Split('\n'))
            {
                text.Append("> ").Append(line.TrimEnd()).Append('\n');
            }

            text.Append('\n').Append(note.Text.Trim().Length > 0 ? note.Text.Trim() : "(sin nota)").Append("\n\n");
        }

        return text.ToString().TrimEnd() + "\n";
    }
}
