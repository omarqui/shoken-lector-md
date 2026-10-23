using LectorMd.Core.Settings;

namespace LectorMd.Core.Tests;

public sealed class EngineChoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lectormd-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string ActiveEngine(EngineChoice choice)
    {
        // Claves falsas: crear los motores no llama a la red.
        var settings = new AppSettings
        {
            AzureKey = "azure-falsa",
            AzureRegion = "eastus",
            GoogleApiKey = "google-falsa",
            Engine = choice,
        };
        using EngineServices services = EngineServices.Create(settings, new AppPaths(_dir));
        return services.Engine.Status.ActiveEngine;
    }

    [Theory]
    [InlineData(EngineChoice.Auto, "Azure · es-MX-DaliaNeural")]
    [InlineData(EngineChoice.Azure, "Azure · es-MX-DaliaNeural")]
    [InlineData(EngineChoice.Google, "Google · es-US-Neural2-A")]
    public void EngineChoice_PicksTheEngineThatReads(EngineChoice choice, string expected)
    {
        Assert.Equal(expected, ActiveEngine(choice));
    }

    [Fact]
    public void EngineChoice_Piper_WithoutPiperConfigured_HasNoEngine()
    {
        var settings = new AppSettings { AzureKey = "azure-falsa", Engine = EngineChoice.Piper };
        using EngineServices services = EngineServices.Create(settings, new AppPaths(_dir));

        Assert.False(services.Engine.IsConfigured);
    }

    [Fact]
    public void GoogleUsage_HasItsOwnCounterAndLimit()
    {
        var settings = new AppSettings { GoogleApiKey = "google-falsa", GoogleMonthlyCharacterLimit = 1234 };
        using EngineServices services = EngineServices.Create(settings, new AppPaths(_dir));

        Assert.Equal(1234, services.GoogleUsage.Limit);
        Assert.NotSame(services.Usage, services.GoogleUsage);
    }
}
