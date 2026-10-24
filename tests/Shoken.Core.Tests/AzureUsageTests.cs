using Shoken.Core.Usage;

namespace Shoken.Core.Tests;

public sealed class AzureUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shoken-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MetricsUrl_CoversTheCurrentUtcMonth()
    {
        string url = AzureUsageClient.BuildMetricsUrl(
            "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/voz",
            new DateTime(2026, 10, 23, 15, 30, 0, DateTimeKind.Utc));

        Assert.StartsWith("https://management.azure.com/subscriptions/s1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/voz/providers/Microsoft.Insights/metrics?", url);
        Assert.Contains("metricnames=SynthesizedCharacters", url);
        Assert.Contains("aggregation=Total", url);
        Assert.Contains("timespan=2026-10-01T00:00:00Z/2026-10-23T15:30:00Z", url);
    }

    [Fact]
    public void ParseMetricTotal_AddsEveryDay()
    {
        const string json = """
            {"value":[{"name":{"value":"SynthesizedCharacters"},"timeseries":[{"data":[
              {"timeStamp":"2026-10-01T00:00:00Z","total":1200.0},
              {"timeStamp":"2026-10-02T00:00:00Z"},
              {"timeStamp":"2026-10-03T00:00:00Z","total":301.0}
            ]}]}]}
            """;

        Assert.Equal(1501, AzureUsageClient.ParseMetricTotal(json));
    }

    [Fact]
    public void ParseMetricTotal_IsZeroWithoutData()
    {
        Assert.Equal(0, AzureUsageClient.ParseMetricTotal("""{"value":[{"timeseries":[]}]}"""));
    }

    [Fact]
    public void ParseSpeechAccounts_KeepsOnlyResourcesThatCanSynthesizeSpeech()
    {
        const string json = """
            {"value":[
              {"id":"/subscriptions/s/a1","name":"voz","location":"eastus","kind":"SpeechServices","sku":{"name":"F0"}},
              {"id":"/subscriptions/s/a2","name":"traductor","location":"eastus","kind":"TextTranslation","sku":{"name":"F0"}},
              {"id":"/subscriptions/s/a3","name":"foundry","location":"westus","kind":"AIServices","sku":{"name":"S0"}}
            ]}
            """;

        IReadOnlyList<SpeechResource> accounts = AzureUsageClient.ParseSpeechAccounts(json, "tenant-1");

        Assert.Equal(["voz", "foundry"], accounts.Select(a => a.Name));
        Assert.Equal("F0", accounts[0].Sku);
        Assert.Equal("tenant-1", accounts[0].TenantId);
    }

    [Theory]
    [InlineData("eastus", "East US", true)]
    [InlineData("southcentralus", "southcentralus", true)]
    [InlineData("eastus", "eastus2", false)]
    public void SpeechResource_MatchesRegionIgnoringCaseAndSpaces(string location, string region, bool expected)
    {
        var resource = new SpeechResource("/id", "voz", location, "SpeechServices", "F0", "t");

        Assert.Equal(expected, resource.IsInRegion(region));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.5)]
    [InlineData(0.04, 0.2)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.7, 1.0)]
    [InlineData(-0.2, 0.0)]
    public void UsageGauge_HighlightsLowValues_AndFillsAtTheLimit(double fraction, double expected)
    {
        Assert.Equal(expected, UsageGauge.FillFraction(fraction), precision: 6);
    }

    [Theory]
    [InlineData("2026-10-23", "2026-11-01")]
    [InlineData("2026-12-31", "2027-01-01")]
    public void Usage_ResetsOnFirstDayOfNextMonth(string today, string expected)
    {
        DateTime now = DateTime.SpecifyKind(DateTime.Parse(today, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var usage = new UsageTracker(Path.Combine(_dir, "uso.json"), 1000, () => now);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), usage.ResetsOn);
        Assert.Equal(DateTimeKind.Utc, usage.ResetsOn.Kind);
    }

    [Fact]
    public void SyncFromService_OnlyRaisesTheLocalCount()
    {
        var now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        var usage = new UsageTracker(Path.Combine(_dir, "uso.json"), 1000, () => now);
        usage.Add(400);

        Assert.True(usage.SyncFromService(650));
        Assert.Equal(650, usage.Used);

        // Azure puede contar menos (no cobra igual el SSML): no se baja la estimación local.
        Assert.False(usage.SyncFromService(100));
        Assert.Equal(650, usage.Used);
    }
}
