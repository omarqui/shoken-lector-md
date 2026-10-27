using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shoken.Core.Planning;
using Shoken.Core.Tts;

namespace Shoken.Core.Caching;

/// <summary>
/// Caché en disco de audio sintetizado. La clave depende del contenido exacto enviado al motor
/// (SSML, voz, velocidad), así que volver a escuchar un documento no gasta cuota de Azure.
/// Las marcas se guardan relativas al bloque (0..n-1) para sobrevivir a cambios en otras partes del documento.
/// </summary>
public sealed class AudioCache
{
    private readonly string _directory;

    public AudioCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;

    public static string ComputeKey(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return Convert.ToHexString(hash, 0, 20).ToLowerInvariant();
    }

    public AudioChunk? TryGet(string key, SpeechChunk chunk, string engineName)
    {
        string metaPath = Path.Combine(_directory, key + ".json");
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(metaPath));
            if (entry is null)
            {
                return null;
            }

            string audioPath = Path.Combine(_directory, key + entry.Extension);
            if (!File.Exists(audioPath) || entry.Marks.Any(m => m.Index < 0 || m.Index >= chunk.Segments.Count))
            {
                return null;
            }

            File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow); // para el recorte por antigüedad
            return new AudioChunk
            {
                FilePath = audioPath,
                Marks = ToMarks(entry.Marks, chunk),
                EngineName = engineName,
                FromCache = true,
                Duration = entry.DurationTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
                Words = ToWords(entry.Words, chunk),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public AudioChunk Store(string key, byte[] audio, string extension, IReadOnlyList<(int Index, TimeSpan Offset)> marks,
        SpeechChunk chunk, string engineName, TimeSpan? duration = null,
        IReadOnlyList<(int Index, TimeSpan Offset, string Text)>? words = null)
    {
        string audioPath = Path.Combine(_directory, key + extension);
        string tempPath = audioPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tempPath, audio);
        File.Move(tempPath, audioPath, overwrite: true);

        // Los tiempos por palabra son datos de la entrada, no parte de la clave: el SSML no cambia.
        List<CachedWord>? cachedWords = words is { Count: > 0 }
            ? words.Where(w => w.Index >= 0 && w.Index < chunk.Segments.Count)
                .Select(w => new CachedWord(w.Index, w.Offset.Ticks, w.Text))
                .ToList()
            : null;
        var entry = new CacheEntry(extension, Complete(marks, chunk.Segments.Count)
            .Select(m => new CachedMark(m.Index, m.Offset.Ticks))
            .ToList(), duration?.Ticks, cachedWords);
        File.WriteAllText(Path.Combine(_directory, key + ".json"), JsonSerializer.Serialize(entry));

        return new AudioChunk
        {
            FilePath = audioPath,
            Marks = ToMarks(entry.Marks, chunk),
            EngineName = engineName,
            FromCache = false,
            Duration = duration,
            Words = ToWords(entry.Words, chunk),
        };
    }

    public long SizeBytes()
    {
        try
        {
            return new DirectoryInfo(_directory).EnumerateFiles().Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public void Clear()
    {
        foreach (string file in Directory.EnumerateFiles(_directory))
        {
            TryDelete(file);
        }
    }

    /// <summary>Borra las entradas menos usadas hasta quedar por debajo de <paramref name="maxBytes"/>.</summary>
    public void Trim(long maxBytes)
    {
        var dir = new DirectoryInfo(_directory);
        var files = dir.EnumerateFiles().ToList();
        long total = files.Sum(f => f.Length);
        if (total <= maxBytes)
        {
            return;
        }

        foreach (FileInfo meta in files.Where(f => f.Extension == ".json").OrderBy(f => f.LastWriteTimeUtc))
        {
            string stem = Path.GetFileNameWithoutExtension(meta.Name);
            foreach (FileInfo file in files.Where(f => Path.GetFileNameWithoutExtension(f.Name) == stem))
            {
                total -= file.Length;
                TryDelete(file.FullName);
            }

            if (total <= maxBytes)
            {
                break;
            }
        }

        // Temporales huérfanos de escrituras interrumpidas.
        foreach (FileInfo tmp in dir.EnumerateFiles("*.tmp"))
        {
            TryDelete(tmp.FullName);
        }
    }

    /// <summary>Garantiza una marca por segmento (si falta alguna, hereda el tiempo de la anterior).</summary>
    private static List<(int Index, TimeSpan Offset)> Complete(IReadOnlyList<(int Index, TimeSpan Offset)> marks, int count)
    {
        var byIndex = new Dictionary<int, TimeSpan>();
        foreach ((int index, TimeSpan offset) in marks)
        {
            if (index >= 0 && index < count && !byIndex.ContainsKey(index))
            {
                byIndex[index] = offset;
            }
        }

        var result = new List<(int, TimeSpan)>(count);
        TimeSpan last = TimeSpan.Zero;
        for (int i = 0; i < count; i++)
        {
            if (byIndex.TryGetValue(i, out TimeSpan offset) && offset >= last)
            {
                last = offset;
            }

            result.Add((i, last));
        }

        return result;
    }

    private static List<SegmentMark> ToMarks(IEnumerable<CachedMark> marks, SpeechChunk chunk) =>
        marks.OrderBy(m => m.OffsetTicks)
            .ThenBy(m => m.Index)
            .Select(m => new SegmentMark(chunk.Segments[m.Index].Id, TimeSpan.FromTicks(m.OffsetTicks)))
            .ToList();

    private static List<WordMark> ToWords(IEnumerable<CachedWord>? words, SpeechChunk chunk) =>
        words is null
            ? []
            : words.Where(w => w.Index >= 0 && w.Index < chunk.Segments.Count)
                .OrderBy(w => w.OffsetTicks)
                .Select(w => new WordMark(chunk.Segments[w.Index].Id, TimeSpan.FromTicks(w.OffsetTicks), w.Text))
                .ToList();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si está en uso (reproduciéndose) se borrará en otra ocasión.
        }
    }

    // DurationTicks y Words faltan en las entradas de versiones anteriores: se deserializan como null.
    private sealed record CacheEntry(string Extension, List<CachedMark> Marks, long? DurationTicks = null, List<CachedWord>? Words = null);

    private sealed record CachedMark(int Index, long OffsetTicks);

    private sealed record CachedWord(int Index, long OffsetTicks, string Text);
}
