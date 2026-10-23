using LectorMd.Core.Text;

namespace LectorMd.Core.Tests;

public sealed class PronunciationEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lectormd-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "pronunciacion.json");

    public PronunciationEditTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "JSON": "yéison", "Azure": "áchur" }""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private Dictionary<string, string> Read() =>
        PronunciationDictionary.FromJson(File.ReadAllText(File_)).Entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public void Upsert_AddsANewTerm_AndKeepsTheOthers()
    {
        PronunciationDictionary.Upsert(File_, "Kubernetes", "kubernetis");

        Dictionary<string, string> entries = Read();
        Assert.Equal("kubernetis", entries["Kubernetes"]);
        Assert.Equal("yéison", entries["JSON"]);
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void Upsert_EditsAnExistingTerm_IgnoringCase_AndKeepsItsSpelling()
    {
        PronunciationDictionary.Upsert(File_, "azure", "ásur");

        Dictionary<string, string> entries = Read();
        Assert.Equal("ásur", entries["Azure"]);
        Assert.Equal(2, entries.Count);
        Assert.Contains("\"Azure\"", File.ReadAllText(File_));
    }

    [Fact]
    public void Upsert_WithNull_RemovesTheTerm()
    {
        PronunciationDictionary.Upsert(File_, "JSON", null);

        Assert.False(Read().ContainsKey("JSON"));
    }

    [Fact]
    public void Upsert_CreatesTheFileFromTheDefaults_WhenItDoesNotExist()
    {
        File.Delete(File_);

        PronunciationDictionary.Upsert(File_, "Lector", "léctor");

        Dictionary<string, string> entries = Read();
        Assert.Equal("léctor", entries["Lector"]);
        Assert.True(entries.ContainsKey("Markdown")); // los valores por defecto siguen ahí
    }

    [Fact]
    public void WithEntry_ReturnsACopy_WithoutTouchingTheOriginal()
    {
        var original = new PronunciationDictionary(new Dictionary<string, string> { ["JSON"] = "yéison" });

        PronunciationDictionary preview = original.WithEntry("Azure", "áchur");

        Assert.Equal("áchur", preview.Apply("Azure"));
        Assert.Equal("Azure", original.Apply("Azure"));
    }
}
