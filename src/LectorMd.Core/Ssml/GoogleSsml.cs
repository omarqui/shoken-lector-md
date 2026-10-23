using System.Globalization;
using System.Xml.Linq;

namespace LectorMd.Core.Ssml;

/// <summary>
/// Traduce el SSML que arma <see cref="SsmlBuilder"/> (dialecto de Azure) al de Google Cloud TTS:
/// las marcas son <c>&lt;mark name&gt;</c>, la voz va fuera del SSML y la prosodia usa otras unidades.
/// </summary>
public static class GoogleSsml
{
    public static string FromAzure(string azureSsml)
    {
        XElement source = XElement.Parse(azureSsml);
        XElement content = source.Elements().FirstOrDefault(e => e.Name.LocalName == "voice") ?? source;
        return new XElement("speak", content.Nodes().Select(Convert)).ToString(SaveOptions.DisableFormatting);
    }

    private static XNode Convert(XNode node)
    {
        if (node is not XElement element)
        {
            return node is XText text ? new XText(text.Value) : node;
        }

        string name = element.Name.LocalName;
        if (name == "bookmark")
        {
            return new XElement("mark", new XAttribute("name", (string?)element.Attribute("mark") ?? ""));
        }

        var converted = new XElement(name);
        foreach (XAttribute attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
        {
            string value = name == "prosody" ? ConvertProsody(attribute.Name.LocalName, attribute.Value) : attribute.Value;
            converted.Add(new XAttribute(attribute.Name.LocalName, value));
        }

        converted.Add(element.Nodes().Select(Convert));
        return converted;
    }

    /// <summary>
    /// Azure usa cambios relativos ("-4%"); Google entiende la velocidad como porcentaje de lo normal ("96%")
    /// y el tono mejor en semitonos ("+1st").
    /// </summary>
    private static string ConvertProsody(string attribute, string value)
    {
        if (!value.EndsWith('%') || !double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        {
            return value;
        }

        if (attribute == "rate")
        {
            return (100 + percent).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        if (attribute == "pitch")
        {
            double semitones = Math.Round(12 * Math.Log2(1 + percent / 100), 1);
            return (semitones >= 0 ? "+" : "") + semitones.ToString("0.#", CultureInfo.InvariantCulture) + "st";
        }

        return value;
    }
}
