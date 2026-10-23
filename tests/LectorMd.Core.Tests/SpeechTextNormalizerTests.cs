using LectorMd.Core.Text;

namespace LectorMd.Core.Tests;

public class SpeechTextNormalizerTests
{
    private readonly SpeechTextNormalizer _normalizer = new();

    [Theory]
    [InlineData("Proyecto en C# y .NET", "Proyecto en si sharp y dot net")]
    [InlineData("Usa ASP.NET Core", "Usa a ese pe dot net Core")]
    [InlineData("El archivo JSON", "El archivo yéison")]
    [InlineData("Ver el README, etc.", "Ver el ríd mi, etcétera")]
    public void Dictionary_ReplacesWholeTerms(string input, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(input));
    }

    [Fact]
    public void Dictionary_DoesNotReplaceInsideWords()
    {
        Assert.Equal("Squirrel y CSSOM", _normalizer.Normalize("Squirrel y CSSOM"));
    }

    [Fact]
    public void Checkmarks_BecomeSiAndNo_AndOtherEmojisAreRemoved()
    {
        Assert.Equal("Azure: sí. Piper: no.", _normalizer.Normalize("Azure: ✅. Piper: ❌."));
        Assert.Equal("Listo para despegar", _normalizer.Normalize("Listo para despegar 🚀"));
    }

    [Fact]
    public void ArrowsAndUrls_AreSpokenNaturally()
    {
        Assert.Equal("Archivo, Abrir", _normalizer.Normalize("Archivo → Abrir"));
        Assert.Equal("Visita enlace hoy", _normalizer.Normalize("Visita https://ejemplo.com/a?b=1 hoy"));
        Assert.Equal("tarda aproximadamente 5 minutos", _normalizer.Normalize("tarda ~5 minutos"));
    }

    [Fact]
    public void EdgeSpaces_ArePreserved()
    {
        Assert.Equal(" hola ", _normalizer.Normalize(" hola "));
        Assert.Equal(" ", _normalizer.Normalize("   "));
    }

    [Theory]
    [InlineData("Instalación", "Instalación.")]
    [InlineData("¿Listo?", "¿Listo?")]
    [InlineData("Pasos:", "Pasos:")]
    [InlineData("uno, ", "uno.")]
    public void EnsureTerminalPunctuation_ClosesTheSentence(string input, string expected)
    {
        Assert.Equal(expected, SpeechTextNormalizer.EnsureTerminalPunctuation(input));
    }

    [Fact]
    public void CustomDictionary_FromJsonWithComments()
    {
        var dictionary = PronunciationDictionary.FromJson("{ // comentario\n \"Docker\": \"dóker\", }");
        var normalizer = new SpeechTextNormalizer(dictionary);

        Assert.Equal("Instala dóker", normalizer.Normalize("Instala Docker"));
    }
}
