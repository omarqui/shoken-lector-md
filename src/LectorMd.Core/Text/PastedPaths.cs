namespace LectorMd.Core.Text;

/// <summary>Reconoce rutas de archivo en un texto pegado (como las copia el Explorador, con comillas, o como file:///).</summary>
public static class PastedPaths
{
    private const int MaxLines = 100;

    /// <summary>
    /// Rutas absolutas, una por línea y sin repetir. Si alguna línea no es una ruta, el texto no es una
    /// lista de rutas (por ejemplo, un Markdown que menciona una) y se devuelve una lista vacía.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string[] lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > MaxLines)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (string line in lines)
        {
            if (ToPath(line) is not { } path)
            {
                return [];
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static string? ToPath(string line)
    {
        string candidate = line.Trim().Trim('"', '\'').Trim();
        if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            {
                return null;
            }

            candidate = uri.LocalPath;
        }

        candidate = Environment.ExpandEnvironmentVariables(candidate);
        if (!Path.IsPathFullyQualified(candidate) || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
