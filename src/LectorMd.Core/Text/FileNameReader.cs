using System.Text;
using System.Text.RegularExpressions;

namespace LectorMd.Core.Text;

/// <summary>
/// Lee nombres de archivo como se dicen en voz alta: "settings.json" → "settings punto yéison".
/// Solo reconoce extensiones conocidas o cortas sin vocales (que se deletrean), para no tocar
/// oraciones mal espaciadas ("Terminé.Luego") ni nombres como "LectorMd.App".
/// </summary>
internal static class FileNameReader
{
    // Nombre (con o sin puntos intermedios) + punto + extensión, como palabra completa.
    private static readonly Regex FileName = new(
        @"(?<![\p{L}\p{N}_.\-])(?<name>[\p{L}\p{N}_\-]+(?:\.[\p{L}\p{N}_\-]+)*)\.(?<ext>\p{L}[\p{L}\p{N}]{1,7})(?![\p{L}\p{N}_\-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"] = "eme de",
        ["mdx"] = "eme de equis",
        ["markdown"] = "márkdaun",
        ["txt"] = "te equis te",
        ["json"] = "yéison",
        ["jsonl"] = "yéison ele",
        ["yaml"] = "yámel",
        ["yml"] = "yámel",
        ["xml"] = "equis eme ele",
        ["xaml"] = "sámel",
        ["html"] = "hache te eme ele",
        ["htm"] = "hache te eme",
        ["css"] = "ce ese ese",
        ["js"] = "yei es",
        ["mjs"] = "eme yei es",
        ["cjs"] = "ce yei es",
        ["ts"] = "te ese",
        ["tsx"] = "te ese equis",
        ["jsx"] = "yei ese equis",
        ["cs"] = "ce ese",
        ["csproj"] = "ce ese proy",
        ["sln"] = "ese ele ene",
        ["props"] = "props",
        ["py"] = "pe i",
        ["ps1"] = "pe ese uno",
        ["sh"] = "ese hache",
        ["bat"] = "bat",
        ["cmd"] = "ce eme de",
        ["exe"] = "e equis e",
        ["dll"] = "de ele ele",
        ["sql"] = "ese cu ele",
        ["csv"] = "ce ese ve",
        ["pdf"] = "pe de efe",
        ["png"] = "pe ene gue",
        ["jpg"] = "jota pe gue",
        ["jpeg"] = "yei peg",
        ["gif"] = "gif",
        ["svg"] = "ese ve gue",
        ["mp3"] = "eme pe tres",
        ["wav"] = "uav",
        ["zip"] = "zip",
        ["log"] = "log",
        ["config"] = "config",
        ["ini"] = "ini",
        ["toml"] = "tóml",
        ["lock"] = "lok",
        ["docx"] = "doc equis",
        ["xlsx"] = "equis ele ese equis",
        ["pptx"] = "pe pe te equis",
        ["onnx"] = "ónix",
        ["dacpac"] = "dac pac",
        ["bak"] = "bak",
        // Visual FoxPro
        ["prg"] = "pe erre gue",
        ["scx"] = "ese ce equis",
        ["vcx"] = "ve ce equis",
        ["frx"] = "efe erre equis",
        ["dbf"] = "de be efe",
        ["cdx"] = "ce de equis",
        ["fxp"] = "efe equis pe",
    };

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['b'] = "be", ['c'] = "ce", ['d'] = "de", ['f'] = "efe", ['g'] = "gue", ['h'] = "hache", ['j'] = "jota",
        ['k'] = "ka", ['l'] = "ele", ['m'] = "eme", ['n'] = "ene", ['p'] = "pe", ['q'] = "cu", ['r'] = "erre",
        ['s'] = "ese", ['t'] = "te", ['v'] = "ve", ['w'] = "doble u", ['x'] = "equis", ['y'] = "i griega", ['z'] = "zeta",
        ['0'] = "cero", ['1'] = "uno", ['2'] = "dos", ['3'] = "tres", ['4'] = "cuatro",
        ['5'] = "cinco", ['6'] = "seis", ['7'] = "siete", ['8'] = "ocho", ['9'] = "nueve",
    };

    /// <param name="isDictionaryTerm">Términos que ya tienen su propia pronunciación ("Node.js"): no se tocan.</param>
    public static string Apply(string text, Func<string, bool> isDictionaryTerm)
    {
        if (text.IndexOf('.') < 0)
        {
            return text;
        }

        return FileName.Replace(text, match =>
        {
            if (isDictionaryTerm(match.Value) || Pronounce(match.Groups["ext"].Value) is not { } extension)
            {
                return match.Value;
            }

            return match.Groups["name"].Value.Replace(".", " punto ", StringComparison.Ordinal) + " punto " + extension;
        });
    }

    private static string? Pronounce(string extension)
    {
        if (Extensions.TryGetValue(extension, out string? known))
        {
            return known;
        }

        // Una extensión corta sin vocales no se puede leer como palabra: se deletrea.
        string lower = extension.ToLowerInvariant();
        if (lower.Length > 4 || lower.IndexOfAny(['a', 'e', 'i', 'o', 'u']) >= 0 || !lower.All(Letters.ContainsKey))
        {
            return null;
        }

        var spelled = new StringBuilder();
        foreach (char c in lower)
        {
            spelled.Append(spelled.Length > 0 ? " " : "").Append(Letters[c]);
        }

        return spelled.ToString();
    }
}
