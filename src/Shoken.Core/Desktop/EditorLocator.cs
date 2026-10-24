namespace Shoken.Core.Desktop;

/// <summary>Dónde está el editor con el que se abre un documento para corregirlo.</summary>
public static class EditorLocator
{
    /// <summary>
    /// Code.exe de VS Code: junto al comando <c>code</c> del PATH (que vive en su carpeta bin), o en una de las
    /// carpetas donde suele instalarse. Null si no está.
    /// </summary>
    public static string? FindVsCode(string? pathVariable, IEnumerable<string> usualLocations, Func<string, bool> fileExists)
    {
        foreach (string folder in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string command, exe;
            try
            {
                command = Path.Combine(folder, "code.cmd");
                exe = Path.GetFullPath(Path.Combine(folder, "..", "Code.exe"));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue; // una entrada rota del PATH no impide buscar en las demás
            }

            if (fileExists(command) && fileExists(exe))
            {
                return exe;
            }
        }

        return usualLocations.FirstOrDefault(fileExists);
    }
}
