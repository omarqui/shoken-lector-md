using System.Globalization;

namespace Shoken.Core.Settings;

/// <summary>Temas de color; cada uno tiene su versión clara y oscura (el botón sol/luna elige cuál se ve).</summary>
public enum ThemePalette
{
    /// <summary>El diseño original (propuesta D): blanco y grises neutros.</summary>
    Classic,

    /// <summary>Blanco hueso cálido y carbón cálido: menos brillo para leer mucho rato.</summary>
    Paper,

    /// <summary>Grises neutros algo azulados, como GitHub o VS Code.</summary>
    Graphite,

    /// <summary>Sepia de libro en claro y azul noche con acento ámbar en oscuro.</summary>
    SepiaNight,
}

/// <summary>
/// Colores de un tema en un modo. Los de la ventana son los recursos Lector*Brush; los del documento, las variables
/// de lector.css (ver <see cref="ThemePalettes.DocumentVariables"/>).
/// </summary>
public sealed record PaletteColors(
    string Background, string Surface, string Surface2, string Border, string Text, string Text2,
    string Accent, string OnAccent, string Pasted, string Current, string CurrentText, string Track,
    string Document, string DocumentText, string Reading, string ReadingEdge, string Code);

/// <summary>Los colores de cada tema: la única fuente para la ventana (ThemeService) y el documento (lector.css).</summary>
public static class ThemePalettes
{
    public static PaletteColors Get(ThemePalette palette, bool dark) => (palette, dark) switch
    {
        (ThemePalette.Paper, false) => new(
            "#EFEBE3", "#F8F5EF", "#EAE5DB", "#DED7CA", "#2B2824", "#6B6459",
            "#2F6F8F", "#FFFFFF", "#7A4FB5", "#DCE8EA", "#1F4E66", "#DCD5C8",
            "#FBF9F4", "#2B2824", "#E4EDEB", "#2F6F8F", "#EFEAE0"),
        (ThemePalette.Paper, true) => new(
            "#1C1B19", "#252421", "#302E2A", "#3A3833", "#ECE7DE", "#B3AC9F",
            "#7CC4D8", "#10303B", "#C3A6E8", "#23393F", "#A9E3F0", "#45423C",
            "#262522", "#ECE7DE", "#2A3A3B", "#7CC4D8", "#2E2C28"),
        (ThemePalette.Graphite, false) => new(
            "#E9ECF0", "#F6F7F9", "#E4E8ED", "#D5DAE1", "#1F2328", "#59636E",
            "#0969DA", "#FFFFFF", "#8250DF", "#DCE8F8", "#0A4A9C", "#D3D9E0",
            "#F6F7F9", "#1F2328", "#E3ECF8", "#0969DA", "#EBEEF2"),
        (ThemePalette.Graphite, true) => new(
            "#1B1F24", "#22272E", "#2D333B", "#373E47", "#D7DEE6", "#9AA6B2",
            "#539BF5", "#0D1A2B", "#B392F0", "#233247", "#9CC7FF", "#3B434D",
            "#22272E", "#D7DEE6", "#25344A", "#539BF5", "#2B3139"),
        (ThemePalette.SepiaNight, false) => new(
            "#EDE3CF", "#F6EEDC", "#EADFC8", "#DCCFB4", "#3A2F23", "#6E5F4B",
            "#9A5B1E", "#FFFFFF", "#7A4FA0", "#EFDDBC", "#6B3D10", "#DCCDB0",
            "#F8F1E2", "#3A2F23", "#F1E2C4", "#9A5B1E", "#EFE4CE"),
        (ThemePalette.SepiaNight, true) => new(
            "#121722", "#182030", "#222B3D", "#2A3447", "#DCE3EE", "#9AA7BC",
            "#F2B35B", "#2A1A05", "#B9A2F0", "#263049", "#F6C77F", "#2F3A50",
            "#182030", "#DCE3EE", "#24304A", "#F2B35B", "#1E2738"),
        (_, false) => new(
            "#F3F3F3", "#FFFFFF", "#F0F0F0", "#E5E5E5", "#1B1B1B", "#5D5D5D",
            "#005FB8", "#FFFFFF", "#8250DF", "#DCE9F7", "#003D7A", "#E0E0E0",
            "#FFFFFF", "#1B1B1B", "#EAF2FB", "#005FB8", "#F3F3F3"),
        (_, true) => new(
            "#202020", "#2B2B2B", "#363636", "#3D3D3D", "#FFFFFF", "#C8C8C8",
            "#60CDFF", "#00344D", "#C297FF", "#1E3A4C", "#99EBFF", "#4A4A4A",
            "#2B2B2B", "#F3F3F3", "#1E3A4C", "#60CDFF", "#333333"),
    };

    /// <summary>
    /// Variables CSS del documento (sin el "--"). Las de la búsqueda y las alertas dependen solo del modo: se leen
    /// bien sobre cualquiera de los fondos.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DocumentVariables(PaletteColors c, bool dark) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bg"] = c.Document,
            ["fg"] = c.DocumentText,
            ["muted"] = c.Text2,
            ["border"] = c.Border,
            ["soft"] = c.Code,
            ["accent"] = c.Accent,
            ["accent-fg"] = c.OnAccent,
            ["reading"] = c.Reading,
            ["reading-edge"] = c.ReadingEdge,
            ["find"] = dark ? "#5C4D12" : "#FFF0A8",
            ["find-current"] = dark ? "#D68A1E" : "#F2A33A",
            ["find-current-fg"] = "#1B1B1B",
            ["note"] = dark ? "#4493F8" : "#0969DA",
            ["tip"] = dark ? "#3FB950" : "#1A7F37",
            ["important"] = dark ? "#AB7DF8" : "#8250DF",
            ["warning"] = dark ? "#D29922" : "#9A6700",
            ["caution"] = dark ? "#F85149" : "#CF222E",
            // Apuntes: el tono del subrayado y el color de la etiqueta de cada tipo (Aclarar, Mejorar, Otro).
            ["annot-clarify"] = dark ? "#3A2C5A" : "#EADFFB",
            ["annot-clarify-edge"] = dark ? "#C4A8FF" : "#6E3FC8",
            ["annot-improve"] = dark ? "#1E3B2A" : "#D9F2E1",
            ["annot-improve-edge"] = dark ? "#6DD58C" : "#176F30",
            ["annot-other"] = dark ? "#33373D" : "#E8EAED",
            ["annot-other-edge"] = dark ? "#B1BAC4" : "#57606A",
        };
}

/// <summary>Contraste entre dos colores según WCAG 2 (1 a 21).</summary>
public static class ColorContrast
{
    public static double Ratio(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        int rgb = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static double Channel(int value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }
}
