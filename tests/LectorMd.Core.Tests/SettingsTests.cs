using System.Text.Json;
using System.Text.Json.Serialization;
using LectorMd.Core.Settings;

namespace LectorMd.Core.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData(1.1, 1.0)]
    [InlineData(1.4, 1.5)]
    [InlineData(3.0, 1.75)]
    [InlineData(0.1, 0.75)]
    [InlineData(1.25, 1.25)]
    public void PlaybackSpeeds_NearestSnapsToAStep(double value, double expected)
    {
        Assert.Equal(expected, PlaybackSpeeds.Nearest(value));
    }

    [Fact]
    public void PlaybackSpeeds_AreTheFiveSelectorSteps()
    {
        Assert.Equal([0.75, 1.0, 1.25, 1.5, 1.75], PlaybackSpeeds.Steps);
    }

    [Fact]
    public void Theme_DefaultsToSystem_WhenMissingFromSavedSettings()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"AzureRegion":"eastus"}""", options)!;

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Contains("\"Theme\":\"Dark\"", JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.Dark }, options));
    }
}
