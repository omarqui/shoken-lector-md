using System.Text.Json.Serialization;

namespace Shoken.Core.Settings;

/// <summary>Una pestaña abierta, tal como se guarda para volver a abrirla al iniciar la app.</summary>
public sealed class OpenTab
{
    /// <summary>Ruta del documento. Vacía en una pestaña nueva a la que todavía no se le pegó nada.</summary>
    public string Path { get; set; } = "";

    /// <summary>True si el documento es texto pegado, guardado por la app en su carpeta "pegados".</summary>
    public bool IsPasted { get; set; }

    /// <summary>Nombre de la pestaña de un texto pegado (su primer título). Los archivos usan su nombre.</summary>
    public string? Title { get; set; }

    /// <summary>Párrafo donde seguir leyendo, o -1 para empezar desde el principio.</summary>
    public int ResumeSegmentId { get; set; } = -1;

    /// <summary>Huella del bloque donde seguir leyendo, para encontrarlo aunque el archivo cambie (null si no se guardó).</summary>
    public string? ResumeBlockKey { get; set; }

    /// <summary>Parte del bloque donde seguir leyendo (0 si es el primer segmento o el bloque no se partió).</summary>
    public int ResumePart { get; set; }

    /// <summary>Desplazamiento vertical del documento, en píxeles.</summary>
    public double ScrollY { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Path);

    [JsonIgnore]
    public string Name => IsEmpty ? "Nueva pestaña"
        : IsPasted ? Title ?? "Pegado"
        : System.IO.Path.GetFileNameWithoutExtension(Path);

    public OpenTab Clone() => (OpenTab)MemberwiseClone();

    /// <summary>Nombre de archivo para guardar un texto pegado: su título sin caracteres que Windows no acepta.</summary>
    public static string SuggestFileName(string title)
    {
        string clean = new(title.Where(c => !char.IsControl(c) && "<>:\"/\\|?*".IndexOf(c) < 0).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        if (clean.Length > 80)
        {
            clean = clean[..80].TrimEnd('.', ' ');
        }

        return (clean.Length == 0 ? "Pegado" : clean) + ".md";
    }
}

public sealed record RestoredTabs(IReadOnlyList<OpenTab> Tabs, int ActiveIndex, IReadOnlyList<string> Missing);

public static class TabSession
{
    /// <summary>
    /// Pestañas a reabrir al iniciar: las guardadas cuyo archivo todavía existe, sin repetidas.
    /// Si no hay ninguna guardada, se usa el último archivo de la versión sin pestañas.
    /// </summary>
    public static RestoredTabs Restore(AppSettings settings, Func<string, bool> fileExists)
    {
        List<OpenTab> saved = settings.OpenTabs.Count > 0
            ? settings.OpenTabs
            : settings.LastFile is { Length: > 0 } last ? [new OpenTab { Path = last }] : [];

        var tabs = new List<OpenTab>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int activeIndex = -1;

        for (int i = 0; i < saved.Count; i++)
        {
            OpenTab tab = saved[i];
            bool kept = false;
            if (!tab.IsEmpty && seen.Add(tab.Path))
            {
                kept = fileExists(tab.Path);
                if (!kept && !tab.IsPasted)
                {
                    missing.Add(tab.Path);
                }
            }

            // Si la activa ya no está, queda activa la que ocupa su lugar (o la última, si era la última).
            if (i == settings.ActiveTab)
            {
                activeIndex = tabs.Count;
            }

            if (kept)
            {
                tabs.Add(tab.Clone());
            }
        }

        activeIndex = tabs.Count == 0 ? -1 : activeIndex < 0 ? 0 : Math.Min(activeIndex, tabs.Count - 1);
        return new RestoredTabs(tabs, activeIndex, missing);
    }
}
