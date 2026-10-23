using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LectorMd.Core.Text;

/// <summary>
/// Reemplazos de palabra completa para que la voz pronuncie bien siglas, términos técnicos y abreviaturas.
/// Se guarda como un JSON editable por el usuario: <c>{ "JSON": "yéison", "C#": "si sharp" }</c>.
/// </summary>
public sealed class PronunciationDictionary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, string> _entries;
    private readonly Regex? _regex;

    public PronunciationDictionary(IEnumerable<KeyValuePair<string, string>> entries)
    {
        _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in entries)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _entries[key.Trim()] = value ?? "";
            }
        }

        if (_entries.Count > 0)
        {
            string alternation = string.Join("|", _entries.Keys
                .OrderByDescending(k => k.Length)
                .Select(Regex.Escape));

            // "Palabra completa": ni letra, ni dígito, ni guion bajo pegados antes o después.
            _regex = new Regex(
                $@"(?<![\p{{L}}\p{{Nd}}_])(?:{alternation})(?![\p{{L}}\p{{Nd}}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>
    /// Entradas iniciales pensadas para documentación técnica leída en español de México.
    /// No incluyas palabras que también existan en español (local, base, control, real, plan...):
    /// la comparación ignora mayúsculas y las reemplazaría en cualquier texto.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        // Lenguajes, formatos y plataformas
        ["C#"] = "si sharp",
        ["F#"] = "ef sharp",
        [".NET"] = "dot net",
        ["ASP.NET"] = "a ese pe dot net",
        ["dotnet"] = "dot net",
        ["JSON"] = "yéison",
        ["YAML"] = "yámel",
        ["XAML"] = "sámel",
        ["XML"] = "equis eme ele",
        ["CSV"] = "ce ese ve",
        ["PDF"] = "pe de efe",
        ["PNG"] = "pe ene gue",
        ["SVG"] = "ese ve gue",
        ["UTF-8"] = "u te efe ocho",
        ["Markdown"] = "márkdaun",
        ["Markdig"] = "márk dig",
        ["README"] = "ríd mi",
        ["changelog"] = "chéinch log",
        ["JavaScript"] = "yava escript",
        ["TypeScript"] = "taip escript",
        ["Node.js"] = "nod yei es",
        ["Python"] = "páiton",
        ["PowerShell"] = "páuer shel",
        ["SSML"] = "ese ese eme ele",
        ["WPF"] = "doble u pe efe",
        ["WebView2"] = "uéb viú dos",
        ["CefSharp"] = "cef sharp",
        ["wwDotNetBridge"] = "doble u doble u dot net brich",
        ["Visual FoxPro"] = "vísual fox pro",
        ["FoxPro"] = "fox pro",
        ["VFP"] = "ve efe pe",
        ["FoxUnit"] = "fox iúnit",
        ["xUnit"] = "equis iúnit",
        ["Visual Studio"] = "vísual estudio",
        ["VS Code"] = "ve ese cod",
        ["VSCode"] = "ve ese cod",
        ["SQL Server"] = "ese cu ele sérver",
        ["DACPAC"] = "dac pac",
        ["Windows"] = "uíndous",
        ["macOS"] = "mac o ese",
        ["Microsoft"] = "máicrosoft",
        ["Azure"] = "ásher",
        ["Docker"] = "dóker",
        ["Kubernetes"] = "kubernetis",
        ["Chrome"] = "crom",
        ["Edge"] = "ech",
        ["Piper"] = "páiper",

        // Git y GitHub
        ["Git"] = "guit",
        ["GitHub"] = "guit jab",
        ["GitLab"] = "guit lab",
        ["GitHub Actions"] = "guit jab ákshons",
        ["commit"] = "comit",
        ["commits"] = "comits",
        ["merge"] = "merch",
        ["rebase"] = "ribéis",
        ["checkout"] = "chékaut",
        ["pull request"] = "pul ricuést",
        ["pull"] = "pul",
        ["PR"] = "pe erre",
        ["PRs"] = "pe erres",
        ["worktree"] = "uórk tri",
        ["worktrees"] = "uórk tris",
        ["workflow"] = "uórkflou",
        ["workflows"] = "uórkflous",
        ["release"] = "rilís",
        ["releases"] = "rilíses",
        ["hotfix"] = "jótfix",
        ["feature"] = "fícher",
        ["features"] = "fíchers",
        ["issue"] = "íshu",
        ["issues"] = "íshus",
        ["review"] = "riviú",
        ["CI"] = "ce i",
        ["CI/CD"] = "ce i ce de",

        // Desarrollo
        ["build"] = "bild",
        ["builds"] = "bilds",
        ["deploy"] = "diplói",
        ["debug"] = "dibág",
        ["bug"] = "bag",
        ["bugs"] = "bags",
        ["script"] = "escript",
        ["scripts"] = "escripts",
        ["framework"] = "fréimuork",
        ["runtime"] = "rántaim",
        ["backend"] = "bákend",
        ["frontend"] = "frónt end",
        ["endpoint"] = "éndpoint",
        ["endpoints"] = "éndpoints",
        ["localhost"] = "local jost",
        ["server"] = "sérver",
        ["pipeline"] = "páiplain",
        ["runner"] = "ráner",
        ["plugin"] = "plágin",
        ["plugins"] = "plágins",
        ["hook"] = "juk",
        ["hooks"] = "juks",
        ["skill"] = "eskil",
        ["skills"] = "eskils",
        ["agent"] = "éiyent",
        ["agents"] = "éiyents",
        ["cache"] = "cash",
        ["timeout"] = "táimaut",
        ["snapshot"] = "esnápshot",
        ["baseline"] = "béislain",
        ["backfill"] = "bákfil",
        ["legacy"] = "légasi",
        ["template"] = "témpleit",
        ["layout"] = "léiaut",
        ["default"] = "difólt",
        ["query"] = "kuéri",
        ["string"] = "estring",
        ["array"] = "arréi",
        ["null"] = "nul",
        ["input"] = "ínput",
        ["output"] = "áutput",
        ["inline"] = "ínlain",
        ["helper"] = "jélper",
        ["helpers"] = "jélpers",
        ["handler"] = "jándler",
        ["service"] = "sérvis",
        ["repository"] = "ripósitori",
        ["manager"] = "mánayer",
        ["container"] = "contéiner",
        ["spec"] = "espec",
        ["specs"] = "especs",
        ["sprint"] = "esprint",
        ["scope"] = "escóup",
        ["setup"] = "sétap",
        ["workspace"] = "uórkspeis",
        ["hash"] = "jash",
        ["software"] = "sófuer",
        ["hardware"] = "járduer",
        ["feedback"] = "fídbak",
        ["dashboard"] = "dáshbord",
        ["mockup"] = "mókap",
        ["tooltip"] = "túltip",
        ["textbox"] = "text box",
        ["scroll"] = "escrol",
        ["click"] = "clic",
        ["width"] = "uíd",
        ["height"] = "jáit",
        ["online"] = "ónlain",
        ["offline"] = "óflain",
        ["login"] = "lóguin",
        ["password"] = "pásuord",
        ["email"] = "ímeil",
        ["browser"] = "bráuser",
        ["upload"] = "áplod",
        ["download"] = "dáunlod",
        ["Claude"] = "clod",
        ["Code"] = "cod",
        ["Copilot"] = "cópailot",
        ["OpenAI"] = "ópen ei ai",
        ["ChatGPT"] = "chat ye pe te",

        // Siglas que se deletrean
        ["npm"] = "ene pe eme",
        ["npx"] = "ene pe equis",
        ["SQL"] = "ese cu ele",
        ["HTML"] = "hache te eme ele",
        ["CSS"] = "ce ese ese",
        ["URL"] = "u erre ele",
        ["HTTP"] = "hache te te pe",
        ["HTTPS"] = "hache te te pe ese",
        ["API"] = "a pe i",
        ["APIs"] = "a pe is",
        ["SDK"] = "ese de ca",
        ["CLI"] = "ce ele i",
        ["IDE"] = "i de e",
        ["UI"] = "u i",
        ["UX"] = "u equis",
        ["MCP"] = "eme ce pe",
        ["LLM"] = "ele ele eme",
        ["GPT"] = "ye pe te",
        ["TTS"] = "te te ese",
        ["ERP"] = "e erre pe",
        ["QA"] = "cu a",
        ["MVP"] = "eme ve pe",
        ["DB"] = "de be",
        ["ID"] = "i de",
        ["IDs"] = "i des",
        ["GUID"] = "güid",
        ["UUID"] = "u u i de",
        ["DLL"] = "de ele ele",
        ["DLLs"] = "de ele eles",
        ["DTO"] = "de te o",
        ["SP"] = "ese pe",
        ["SPs"] = "ese pes",
        ["OK"] = "okéi",

        // Teclas
        ["Ctrl"] = "control",
        ["Esc"] = "escape",
        ["Enter"] = "énter",

        // Abreviaturas
        ["e.g."] = "por ejemplo",
        ["i.e."] = "es decir",
        ["p. ej."] = "por ejemplo",
        ["etc."] = "etcétera",
        ["aprox."] = "aproximadamente",
        ["vs."] = "versus",
        ["vs"] = "versus",
    };

    public int Count => _entries.Count;

    public IReadOnlyDictionary<string, string> Entries => _entries;

    public string Apply(string text)
    {
        if (_regex is null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return _regex.Replace(text, m => _entries.TryGetValue(m.Value, out string? replacement) ? replacement : m.Value);
    }

    public static PronunciationDictionary FromJson(string json)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                      ?? new Dictionary<string, string>();
        return new PronunciationDictionary(entries);
    }

    public static string ToJson(IReadOnlyDictionary<string, string> entries) =>
        JsonSerializer.Serialize(entries, JsonOptions);

    /// <summary>Carga el diccionario del usuario; si no existe lo crea con <see cref="Defaults"/>.</summary>
    public static PronunciationDictionary LoadOrCreate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, ToJson(Defaults));
                return new PronunciationDictionary(Defaults);
            }

            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Un JSON mal editado no debe impedir leer: se usan los valores por defecto.
            return new PronunciationDictionary(Defaults);
        }
    }
}
