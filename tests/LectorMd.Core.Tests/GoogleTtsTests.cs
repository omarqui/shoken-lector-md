using System.Xml.Linq;
using LectorMd.Core.Ssml;

namespace LectorMd.Core.Tests;

public class GoogleTtsTests
{
    private const string Azure =
        "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"es-MX\">" +
        "<voice name=\"es-MX-DaliaNeural\"><break time=\"300ms\"/><bookmark mark=\"0\"/>" +
        "<prosody rate=\"-6%\" pitch=\"-3%\">Título.</prosody> <bookmark mark=\"1\"/>Texto con " +
        "<prosody rate=\"-4%\" pitch=\"+6%\">negrita</prosody> &amp; más.</voice></speak>";

    // ------------------------------------------------------------------ SSML

    [Fact]
    public void GoogleSsml_TurnsBookmarksIntoMarks_AndDropsTheVoice()
    {
        XElement speak = XElement.Parse(GoogleSsml.FromAzure(Azure));

        Assert.Equal("speak", speak.Name.LocalName);
        Assert.Equal(XNamespace.None, speak.Name.Namespace);
        Assert.Empty(speak.Descendants().Where(e => e.Name.LocalName is "voice" or "bookmark"));
        Assert.Equal(["0", "1"], speak.Descendants("mark").Select(m => (string?)m.Attribute("name")));
    }

    [Fact]
    public void GoogleSsml_ConvertsRelativeRateToPercentOfNormal_AndPitchToSemitones()
    {
        var prosody = XElement.Parse(GoogleSsml.FromAzure(Azure)).Descendants("prosody").ToList();

        Assert.Equal("94%", (string?)prosody[0].Attribute("rate"));
        Assert.Equal("-0.5st", (string?)prosody[0].Attribute("pitch"));
        Assert.Equal("96%", (string?)prosody[1].Attribute("rate"));
        Assert.Equal("+1st", (string?)prosody[1].Attribute("pitch"));
    }

    [Fact]
    public void GoogleSsml_KeepsBreaksTextAndEscaping()
    {
        string google = GoogleSsml.FromAzure(Azure);

        Assert.Contains("<break time=\"300ms\" />", google.Replace("<break time=\"300ms\"/>", "<break time=\"300ms\" />"));
        Assert.Contains("&amp; más.", google);
        Assert.Contains("Título.", google);
    }
}
