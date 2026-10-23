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

    /// <summary>Entradas iniciales pensadas para documentación técnica leída en español de México.</summary>
    public static IReadOnlyDictionary<string, string> Defaults { get; } = new Dictionary<string, string>
    {
        ["C#"] = "si sharp",
        ["F#"] = "ef sharp",
        [".NET"] = "dot net",
        ["ASP.NET"] = "a ese pe dot net",
        ["JSON"] = "yéison",
        ["YAML"] = "yámel",
        ["README"] = "ríd mi",
        ["GitHub"] = "guit jab",
        ["GitLab"] = "guit lab",
        ["npm"] = "ene pe eme",
        ["SQL"] = "ese cu ele",
        ["HTML"] = "hache te eme ele",
        ["CSS"] = "ce ese ese",
        ["URL"] = "u erre ele",
        ["CLI"] = "ce ele i",
        ["IDE"] = "i de e",
        ["UI"] = "u i",
        ["UX"] = "u equis",
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
