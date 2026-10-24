using System.Text;
using System.Text.RegularExpressions;

namespace Shoken.Core.Text;

/// <summary>
/// Prepara texto para la voz: símbolos y emojis comunes en documentación, URLs, flechas,
/// y el diccionario de pronunciación del usuario.
/// </summary>
public sealed class SpeechTextNormalizer
{
    private static readonly Regex Url = new(@"\b(?:https?://|www\.)[^\s<>()\[\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Approximately = new(@"~(?=\s?\d)", RegexOptions.Compiled);
    private static readonly Regex SnakeCase = new(@"(?<=\p{L})_(?=\p{L})", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+(?=[,.;:!?])", RegexOptions.Compiled);
    private static readonly Regex RepeatedCommas = new(@"(?:,\s*){2,}", RegexOptions.Compiled);

    // El orden importa: las secuencias largas van antes que sus prefijos.
    private static readonly (string From, string To)[] Symbols =
    [
        ("✔️", " sí "), ("✅", " sí "), ("✔", " sí "), ("☑️", " sí "), ("☑", " sí "), ("✓", " sí "),
        ("✖️", " no "), ("❌", " no "), ("✖", " no "), ("✗", " no "),
        ("⚠️", " atención: "), ("⚠", " atención: "),
        ("-->", ", "), ("->", ", "), ("→", ", "), ("=>", ", "), ("⇒", ", "), ("⟶", ", "), ("<-", ", "), ("←", ", "),
        ("≥", " mayor o igual a "), (">=", " mayor o igual a "),
        ("≤", " menor o igual a "), ("<=", " menor o igual a "),
        ("≠", " distinto de "), ("!=", " distinto de "),
        ("≈", " aproximadamente "), ("±", " más menos "),
        ("•", ", "), ("·", ", "), ("|", ", "), ("…", "..."),
        ("&", " y "), ("@", " arroba "),
    ];

    private readonly PronunciationDictionary _dictionary;

    public SpeechTextNormalizer(PronunciationDictionary? dictionary = null)
    {
        _dictionary = dictionary ?? new PronunciationDictionary(PronunciationDictionary.Defaults);
    }

    public PronunciationDictionary Dictionary => _dictionary;

    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrEmpty(text) ? "" : " ";
        }

        bool leadingSpace = char.IsWhiteSpace(text[0]);
        bool trailingSpace = char.IsWhiteSpace(text[^1]);

        string result = Url.Replace(text, "enlace");
        foreach ((string from, string to) in Symbols)
        {
            if (result.Contains(from, StringComparison.Ordinal))
            {
                result = result.Replace(from, to, StringComparison.Ordinal);
            }
        }

        result = Approximately.Replace(result, "aproximadamente ");
        result = SnakeCase.Replace(result, " ");
        result = FileNameReader.Apply(result, term => _dictionary.Entries.ContainsKey(term));
        result = _dictionary.Apply(result);
        result = StripPictographs(result);
        result = Spaces.Replace(result, " ");
        result = SpaceBeforePunctuation.Replace(result, "");
        result = RepeatedCommas.Replace(result, ", ");
        result = result.Trim();

        // Se conservan los espacios de los extremos: separan tramos con estilos distintos.
        if (result.Length == 0)
        {
            return leadingSpace || trailingSpace ? " " : "";
        }

        return (leadingSpace ? " " : "") + result + (trailingSpace ? " " : "");
    }

    /// <summary>Termina el texto con un punto si no acaba en puntuación, para que la entonación "cierre".</summary>
    public static string EnsureTerminalPunctuation(string text)
    {
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        char last = trimmed[^1];
        if (".!?:;…".Contains(last))
        {
            return trimmed;
        }

        // "Hola," → "Hola."
        return last == ',' ? trimmed[..^1] + "." : trimmed + ".";
    }

    /// <summary>Quita emojis y pictogramas que la voz leería por su nombre ("cohete", "cara sonriente").</summary>
    internal static string StripPictographs(string text)
    {
        var builder = new StringBuilder(text.Length);
        int index = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            bool drop = v is 0xFE0F or 0xFE0E or 0x200D or 0x20E3
                        || v is >= 0x1F000 and <= 0x1FAFF
                        || v is >= 0x2600 and <= 0x27BF
                        || v is >= 0x2B00 and <= 0x2BFF
                        || v is >= 0xE0020 and <= 0xE007F;
            if (!drop)
            {
                builder.Append(text, index, rune.Utf16SequenceLength);
            }

            index += rune.Utf16SequenceLength;
        }

        return builder.ToString();
    }
}
