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

    [Theory]
    [InlineData(1.25, 1.0, 1.5)]
    [InlineData(1.0, 0.75, 1.25)]
    [InlineData(1.1, 0.75, 1.25)] // una velocidad vieja fuera de los pasos cuenta como la más cercana (1×)
    public void PlaybackSpeeds_SlowerAndFasterMoveOneStep(double speed, double slower, double faster)
    {
        Assert.Equal(slower, PlaybackSpeeds.Slower(speed));
        Assert.Equal(faster, PlaybackSpeeds.Faster(speed));
    }

    [Fact]
    public void PlaybackSpeeds_StopAtTheEnds()
    {
        Assert.Null(PlaybackSpeeds.Slower(0.75));
        Assert.Null(PlaybackSpeeds.Faster(1.75));
    }

    [Theory]
    [InlineData(1.0, "1×")]
    [InlineData(1.25, "1.25×")]
    [InlineData(0.75, "0.75×")]
    public void PlaybackSpeeds_LabelHasNoTrailingZeros(double speed, string expected)
    {
        Assert.Equal(expected, PlaybackSpeeds.Label(speed));
    }

    [Fact]
    public void PlaybackSpeeds_AreTheFiveSelectorSteps()
    {
        Assert.Equal([0.75, 1.0, 1.25, 1.5, 1.75], PlaybackSpeeds.Steps);
    }

    [Fact]
    public void TextSize_DefaultsToLarge_WhenMissingFromSavedSettings()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        AppSettings settings = JsonSerializer.Deserialize<AppSettings>("""{"AzureRegion":"eastus"}""", options)!;

        Assert.Equal(TextSize.Large, settings.TextSize);
        Assert.Contains("\"TextSize\":\"ExtraLarge\"", JsonSerializer.Serialize(new AppSettings { TextSize = TextSize.ExtraLarge }, options));
    }

    [Theory]
    [InlineData(TextSize.Normal, 12, 12)]
    [InlineData(TextSize.Large, 12, 14)]     // +15 %: 13.8, redondeado a medio punto
    [InlineData(TextSize.Large, 14, 16)]     // 16.1
    [InlineData(TextSize.ExtraLarge, 11, 14.5)] // +30 %: 14.3
    [InlineData(TextSize.ExtraLarge, 14, 18)]   // 18.2
    public void TextSizes_GrowFifteenPercentPerStep_RoundedToHalfAPoint(TextSize size, double baseSize, double expected)
    {
        Assert.Equal(expected, TextSizes.FontSize(size, baseSize));
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
