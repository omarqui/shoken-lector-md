using System.Text.Json;
using System.Text.Json.Serialization;
using LectorMd.Core.Settings;

namespace LectorMd.Core.Tests;

public class ThemeTests
{
    public static TheoryData<ThemePalette, bool> EveryPaletteAndMode()
    {
        var data = new TheoryData<ThemePalette, bool>();
        foreach (ThemePalette palette in Enum.GetValues<ThemePalette>())
        {
            data.Add(palette, false);
            data.Add(palette, true);
        }

        return data;
    }

    [Fact]
    public void Contrast_IsTwentyOneForBlackOnWhite_AndOneForAColorOnItself()
    {
        Assert.Equal(21, ColorContrast.Ratio("#000000", "#FFFFFF"), precision: 2);
        Assert.Equal(1, ColorContrast.Ratio("#2F6F8F", "#2F6F8F"), precision: 2);
    }

    [Theory]
    [MemberData(nameof(EveryPaletteAndMode))]
    public void Palettes_KeepTextReadable_WCAG_AA(ThemePalette palette, bool dark)
    {
        PaletteColors c = ThemePalettes.Get(palette, dark);

        Assert.True(ColorContrast.Ratio(c.DocumentText, c.Document) >= 4.5, "texto del documento");
        Assert.True(ColorContrast.Ratio(c.DocumentText, c.Reading) >= 4.5, "párrafo que se lee");
        Assert.True(ColorContrast.Ratio(c.Text, c.Surface) >= 4.5, "texto de las barras");
        Assert.True(ColorContrast.Ratio(c.Text2, c.Surface) >= 4.5, "texto secundario en las barras");
        Assert.True(ColorContrast.Ratio(c.Text2, c.Background) >= 4.5, "duraciones del índice");
        Assert.True(ColorContrast.Ratio(c.CurrentText, c.Current) >= 4.5, "sección actual del índice");
        Assert.True(ColorContrast.Ratio(c.OnAccent, c.Accent) >= 4.5, "ícono del play");
        Assert.True(ColorContrast.Ratio(c.Accent, c.Surface) >= 3, "acento como ícono o borde");
    }

    [Fact]
    public void Palettes_ClassicIsTheOriginalDesign()
    {
        PaletteColors light = ThemePalettes.Get(ThemePalette.Classic, dark: false);
        PaletteColors dark = ThemePalettes.Get(ThemePalette.Classic, dark: true);

        Assert.Equal(("#F3F3F3", "#FFFFFF", "#005FB8"), (light.Background, light.Document, light.Accent));
        Assert.Equal(("#202020", "#2B2B2B", "#60CDFF"), (dark.Background, dark.Document, dark.Accent));
    }

    [Fact]
    public void Palette_DefaultsToPaper_WhenMissingFromSavedSettings()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"AzureRegion":"eastus"}""", options)!;

        Assert.Equal(ThemePalette.Paper, settings.Palette);
    }

    [Fact]
    public void DocumentVariables_TakeTheDocumentColorsOfThePalette()
    {
        PaletteColors c = ThemePalettes.Get(ThemePalette.Graphite, dark: true);

        IReadOnlyDictionary<string, string> css = ThemePalettes.DocumentVariables(c, dark: true);

        Assert.Equal(c.Document, css["bg"]);
        Assert.Equal(c.DocumentText, css["fg"]);
        Assert.Equal(c.Accent, css["accent"]);
        Assert.Equal(c.Reading, css["reading"]);
        Assert.Equal(c.Code, css["soft"]);
        Assert.Contains("caution", css.Keys); // las alertas y la búsqueda también llegan, según el modo
    }
}
