#:project ../src/Shoken.Core/Shoken.Core.csproj
#:package System.Security.Cryptography.ProtectedData@10.0.0
#:property PublishAot=false
#:property NoWarn=CA1416

// Graba la demo de entonación que trae la app: el texto de src/Shoken.Core/Demo/asi-lee-shoken.md leído por Azure
// (Dalia) con la entonación de Shoken y sin ella. Deja los dos MP3 en src/Shoken.App/Assets/demo y sus datos (marcas,
// duraciones, huellas del SSML) en src/Shoken.Core/Demo/grabacion.json. Hay que volver a grabarla cuando cambia el
// texto o cómo se arma el SSML: la prueba Demo_RecordingMatchesCurrentSsml lo avisa. Uso, desde la raíz del repo:
//   dotnet run empaquetado/generar-demo.cs            graba (gasta unos 2 500 caracteres de la cuota de Azure)
//   dotnet run empaquetado/generar-demo.cs -- --seco  solo muestra los segmentos y el SSML, sin llamar a Azure
// La clave y la región salen de las variables SHOKEN_AZURE_KEY y SHOKEN_AZURE_REGION o, si no están, de la
// configuración de Shoken de este usuario (la clave va cifrada con DPAPI). La clave nunca se muestra.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Shoken.Core.Audio;
using Shoken.Core.Demo;
using Shoken.Core.Settings;
using Shoken.Core.Ssml;
using Shoken.Core.Usage;

bool dryRun = args.Contains("--seco");
string root = Path.GetFullPath(Path.Combine(Here(), ".."));
string assets = Path.Combine(root, "src", "Shoken.App", "Assets", "demo");
string dataPath = Path.Combine(root, "src", "Shoken.Core", "Demo", "grabacion.json");

DemoPlan plan = IntonationDemo.RecordedPlan();
IReadOnlyList<SsmlDocument> con = IntonationDemo.RecordedSsml(plan);
SsmlDocument sin = IntonationDemo.FlatSsml(plan, IntonationDemo.RecordingVoice);
SsmlCost cost = IntonationDemo.MeasureCost(con);

Console.WriteLine($"{plan.Document.Segments.Count} segmentos en {plan.Chunks.Count} bloques.");
foreach (DemoSegment s in IntonationDemo.Describe(plan))
{
    Console.WriteLine($"  [{s.Id}] {s.Kind} pausa={s.PauseBeforeMs} ms{(s.Prosody is { } p ? $" prosodia={p.RatePercent}/{p.PitchPercent}" : "")}" +
                      $"{(s.Strong.Count > 0 ? " negrita=" + string.Join('|', s.Strong) : "")}{(s.AddsPeriod ? " +punto" : "")}" +
                      $"{(s.ReadsNumber ? " número" : "")}{(s.Announces is { } a ? $" dice={a}" : "")} :: {s.Text}");
}

Console.WriteLine($"Con Shoken: {con.Sum(d => d.BillableCharacters):N0} caracteres facturables (texto {cost.Text}, marcas {cost.Bookmarks}, " +
                  $"pausas {cost.Pauses}, prosodia {cost.Prosody}). Sin entonación: {sin.BillableCharacters:N0}.");
if (dryRun)
{
    foreach (SsmlDocument d in con.Append(sin))
    {
        Console.WriteLine();
        Console.WriteLine(d.Ssml);
    }

    return;
}

AppPaths paths = AppPaths.Default;
(string key, string region, int limit) = ReadAzure(paths);
var usage = new UsageTracker(paths.UsageFile, limit);
int needed = con.Sum(d => d.BillableCharacters) + sin.BillableCharacters;
if (!usage.CanSpend(needed))
{
    Console.Error.WriteLine($"No alcanza la cuota del mes: hacen falta {needed:N0} caracteres y quedan {Math.Max(0, usage.Limit - usage.Used):N0}.");
    return;
}

var config = SpeechConfig.FromSubscription(key, region);
config.SpeechSynthesisVoiceName = IntonationDemo.RecordingVoice;
config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3);
using var synthesizer = new SpeechSynthesizer(config, null as AudioConfig);
var marks = new List<(int Index, double Seconds)>();
synthesizer.BookmarkReached += (_, e) =>
{
    lock (marks)
    {
        marks.Add((int.Parse(e.Text, CultureInfo.InvariantCulture), TimeSpan.FromTicks((long)e.AudioOffset).TotalSeconds));
    }
};

// Con Shoken: bloque por bloque, como la app; las marcas del bloque se traducen al segmento y se corren por lo que
// ya sonó.
int count = plan.Document.Segments.Count;
var conMarks = new double[count];
var conParts = new List<byte[]>();
double offset = 0;
for (int i = 0; i < con.Count; i++)
{
    (byte[] audio, double duration, List<(int Index, double Seconds)> chunkMarks) = await SpeakAsync(con[i]);
    foreach ((int index, double seconds) in chunkMarks)
    {
        conMarks[plan.Chunks[i].Segments[index].Id] = Math.Round(offset + seconds, 3);
    }

    conParts.Add(audio);
    offset += duration;
    Console.WriteLine($"Con Shoken, bloque {i + 1} de {con.Count}: {duration:F2} s");
}

// Sin entonación: un solo SSML con un bookmark por segmento.
(byte[] flatAudio, double flatDuration, List<(int Index, double Seconds)> flatMarks) = await SpeakAsync(sin);
var sinMarks = new double[count];
foreach ((int index, double seconds) in flatMarks)
{
    sinMarks[index] = Math.Round(seconds, 3);
}

Console.WriteLine($"Sin entonación: {flatDuration:F2} s");

Directory.CreateDirectory(assets);
File.WriteAllBytes(Path.Combine(assets, "demo-con.mp3"), Mp3Audio.Concatenate(conParts));
File.WriteAllBytes(Path.Combine(assets, "demo-sin.mp3"), flatAudio);
var recording = new DemoRecording(
    IntonationDemo.RecordingVoice,
    new DemoTake("demo-con.mp3", Math.Round(offset, 3), conMarks, con.Select(d => IntonationDemo.Fingerprint(d.Ssml)).ToList(),
        con.Sum(d => d.BillableCharacters)),
    new DemoTake("demo-sin.mp3", Math.Round(flatDuration, 3), sinMarks, [IntonationDemo.Fingerprint(sin.Ssml)], sin.BillableCharacters),
    cost.Text,
    cost.Intonation);
File.WriteAllText(dataPath, JsonSerializer.Serialize(recording, IntonationDemo.RecordingJson) + "\n", new UTF8Encoding(false));
Console.WriteLine($"Listo: {assets} y {dataPath}. Cuota de Azure del mes: {usage.Used:N0} de {usage.Limit:N0}.");

async Task<(byte[] Audio, double Duration, List<(int Index, double Seconds)> Marks)> SpeakAsync(SsmlDocument document)
{
    lock (marks)
    {
        marks.Clear();
    }

    using SpeechSynthesisResult result = await synthesizer.SpeakSsmlAsync(document.Ssml);
    if (result.Reason != ResultReason.SynthesizingAudioCompleted)
    {
        var details = SpeechSynthesisCancellationDetails.FromResult(result);
        throw new InvalidOperationException($"Azure no generó el audio: {details.ErrorCode} {details.ErrorDetails}");
    }

    usage.Add(document.BillableCharacters);
    List<(int, double)> snapshot;
    lock (marks)
    {
        snapshot = [.. marks];
    }

    if (snapshot.Select(m => m.Item1).Distinct().Count() != document.Ssml.Split("<bookmark ").Length - 1)
    {
        throw new InvalidOperationException("Azure no devolvió todas las marcas del bloque.");
    }

    return (result.AudioData, result.AudioDuration.TotalSeconds, snapshot);
}

static (string Key, string Region, int Limit) ReadAzure(AppPaths paths)
{
    string? key = Environment.GetEnvironmentVariable("SHOKEN_AZURE_KEY");
    string? region = Environment.GetEnvironmentVariable("SHOKEN_AZURE_REGION");
    int limit = new AppSettings().MonthlyCharacterLimit;
    if (File.Exists(paths.SettingsFile))
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        if (json.RootElement.TryGetProperty("Settings", out JsonElement settings))
        {
            if (string.IsNullOrWhiteSpace(region) && settings.TryGetProperty("AzureRegion", out JsonElement r))
            {
                region = r.GetString();
            }

            if (settings.TryGetProperty("MonthlyCharacterLimit", out JsonElement l) && l.TryGetInt32(out int value))
            {
                limit = value;
            }
        }

        if (string.IsNullOrWhiteSpace(key) && json.RootElement.TryGetProperty("AzureKeyProtected", out JsonElement k) && k.GetString() is { Length: > 0 } protectedKey)
        {
            // La misma entropía que SettingsStore (conserva el nombre anterior de la app).
            byte[] entropy = "LectorMd.AzureKey.v1"u8.ToArray();
            key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), entropy, DataProtectionScope.CurrentUser));
        }
    }

    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(region))
    {
        throw new InvalidOperationException("Falta la clave o la región de Azure: configúralas en Shoken o en SHOKEN_AZURE_KEY y SHOKEN_AZURE_REGION.");
    }

    return (key.Trim(), region.Trim(), limit);
}

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
