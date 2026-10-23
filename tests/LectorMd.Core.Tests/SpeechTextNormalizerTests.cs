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
    [InlineData("Un archivo Markdown en VS Code", "Un archivo márkdaun en ve ese cod")]
    [InlineData("Haz merge del pull request", "Haz merch del pul ricuést")]
    [InlineData("Pulsa Ctrl + O", "Pulsa control + O")]
    public void Dictionary_ReplacesWholeTerms(string input, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Edita settings.json y AGENTS.md", "Edita settings punto yéison y éiyents punto eme de")]
    [InlineData("Abre README.md.", "Abre ríd mi punto eme de.")]
    [InlineData("Compila LectorMd.App.csproj", "Compila LectorMd punto App punto ce ese proy")]
    [InlineData("Corre main.prg y Deploy.PS1", "Corre main punto pe erre gue y diplói punto pe ese uno")]
    [InlineData("Revisa datos.pqr", "Revisa datos punto pe cu erre")]
    public void FileNames_ReadTheDotAsPuntoAndPronounceTheExtension(string input, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Usa Node.js y ASP.NET", "Usa nod yei es y a ese pe dot net")]
    [InlineData("Ver e.g. la versión 1.2", "Ver por ejemplo la versión 1.2")]
    [InlineData("Terminé.Luego sigo", "Terminé.Luego sigo")]
    [InlineData("El namespace LectorMd.App", "El namespace LectorMd.App")]
    public void FileNames_LeaveDictionaryTermsVersionsAndSentencesAlone(string input, string expected)
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
        Assert.Equal("Nube: sí. Sin conexión: no.", _normalizer.Normalize("Nube: ✅. Sin conexión: ❌."));
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
