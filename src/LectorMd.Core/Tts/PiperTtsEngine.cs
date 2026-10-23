using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LectorMd.Core.Audio;
using LectorMd.Core.Caching;
using LectorMd.Core.Planning;
using LectorMd.Core.Text;

namespace LectorMd.Core.Tts;

public sealed class PiperTtsOptions
{
    public required string ExePath { get; init; }

    /// <summary>Modelo .onnx; su configuración .onnx.json debe estar al lado.</summary>
    public required string ModelPath { get; init; }

    public int RatePercent { get; init; }

    /// <summary>Silencio que Piper agrega tras cada oración.</summary>
    public double SentenceSilenceSeconds { get; init; } = 0.25;
}

/// <summary>
/// Motor de respaldo sin conexión: Piper (voces neuronales locales). Mantiene un proceso
/// <c>piper.exe --json-input</c> vivo; por cada línea JSON <c>{"text","output_file"}</c> escribe un WAV
/// e imprime su ruta en stdout. Las pausas del Markdown se insertan como silencio al unir los WAV.
/// </summary>
public sealed class PiperTtsEngine : ITtsEngine, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly PiperTtsOptions _options;
    private readonly SpeechTextNormalizer _normalizer;
    private readonly AudioCache _cache;
    private readonly string _tempDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private bool _disposed;

    public PiperTtsEngine(PiperTtsOptions options, SpeechTextNormalizer normalizer, AudioCache cache, string tempDirectory)
    {
        _options = options;
        _normalizer = normalizer;
        _cache = cache;
        _tempDirectory = tempDirectory;
        Directory.CreateDirectory(tempDirectory);
    }

    public string Name => $"Piper · {Path.GetFileNameWithoutExtension(_options.ModelPath)}";

    public bool IsConfigured =>
        File.Exists(_options.ExePath) && File.Exists(_options.ModelPath) && File.Exists(_options.ModelPath + ".json");

    private double LengthScale => Math.Round(1.0 / (1.0 + Math.Clamp(_options.RatePercent, -50, 100) / 100.0), 3);

    public AudioChunk? TryGetCached(SpeechChunk chunk) =>
        IsConfigured ? _cache.TryGet(Prepare(chunk).Key, chunk, Name) : null;

    public async Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured)
        {
            throw new TtsException(TtsFailureKind.NotConfigured,
                "Piper no está configurado (falta piper.exe, el modelo .onnx o su .onnx.json).");
        }

        (List<string> texts, string key) = Prepare(chunk);
        if (_cache.TryGet(key, chunk, Name) is { } cached)
        {
            return cached;
        }

        var parts = new List<(WavAudio? Audio, int Gap)>(texts.Count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < texts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int gap = chunk.GapBefore(i);
                if (!HasSpeakableText(texts[i]))
                {
                    parts.Add((null, gap));
                    continue;
                }

                string temp = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    await SynthesizeLineAsync(texts[i], temp, cancellationToken).ConfigureAwait(false);
                    byte[] bytes = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
                    parts.Add((WavAudio.Parse(bytes), gap));
                }
                finally
                {
                    TryDelete(temp);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        WavAudio format = parts.FirstOrDefault(p => p.Audio is not null).Audio
                          ?? WavAudio.Silence(22050, 1, 16, 0);
        var filled = parts
            .Select(p => (p.Audio ?? WavAudio.Silence(format.SampleRate, format.Channels, format.BitsPerSample, 200), p.Gap))
            .ToList();

        WavAudio combined = WavAudio.Concatenate(filled, out IReadOnlyList<TimeSpan> starts);
        var marks = starts.Select((offset, index) => (index, offset)).ToList();
        return _cache.Store(key, combined.ToBytes(), ".wav", marks, chunk, Name, combined.Duration);
    }

    private (List<string> Texts, string Key) Prepare(SpeechChunk chunk)
    {
        var texts = chunk.Segments
            .Select(s => SpeechTextNormalizer.EnsureTerminalPunctuation(
                string.Concat(s.Runs.Select(r => _normalizer.Normalize(r.Text))).Replace('\n', ' ').Trim()))
            .ToList();

        var payload = new StringBuilder();
        for (int i = 0; i < texts.Count; i++)
        {
            payload.Append(chunk.GapBefore(i).ToString(CultureInfo.InvariantCulture)).Append('|').Append(texts[i]).Append('\u001e');
        }

        long modelSize = File.Exists(_options.ModelPath) ? new FileInfo(_options.ModelPath).Length : 0;
        string key = AudioCache.ComputeKey(
            "piper",
            Path.GetFileName(_options.ModelPath),
            modelSize.ToString(CultureInfo.InvariantCulture),
            LengthScale.ToString(CultureInfo.InvariantCulture),
            _options.SentenceSilenceSeconds.ToString(CultureInfo.InvariantCulture),
            payload.ToString());
        return (texts, key);
    }

    private static bool HasSpeakableText(string text) => text.Any(char.IsLetterOrDigit);

    private async Task SynthesizeLineAsync(string text, string outputPath, CancellationToken cancellationToken)
    {
        Process process = EnsureProcess();
        string line = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["text"] = text,
            ["output_file"] = outputPath,
        }, JsonOptions);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, text.Length / 8)));

        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

            // Piper imprime la ruta del WAV cuando termina de escribirlo.
            string? response = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (response is null)
            {
                throw Crash("Piper se cerró inesperadamente.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess();
            throw Crash("Piper no respondió a tiempo.");
        }
        catch (OperationCanceledException)
        {
            // La respuesta pendiente desincronizaría el siguiente pedido: se reinicia el proceso.
            KillProcess();
            throw;
        }
        catch (IOException ex)
        {
            KillProcess();
            throw Crash("No se pudo comunicar con Piper.", ex);
        }

        if (!File.Exists(outputPath))
        {
            throw Crash("Piper no generó el archivo de audio.");
        }
    }

    private Process EnsureProcess()
    {
        if (_process is { HasExited: false })
        {
            return _process;
        }

        _process?.Dispose();
        lock (_stderr)
        {
            _stderr.Clear();
        }

        string exe = Path.GetFullPath(_options.ExePath);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!, // espeak-ng-data está junto a piper.exe
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(Path.GetFullPath(_options.ModelPath));
        start.ArgumentList.Add("--json-input");
        start.ArgumentList.Add("--length_scale");
        start.ArgumentList.Add(LengthScale.ToString("0.###", CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--sentence_silence");
        start.ArgumentList.Add(_options.SentenceSilenceSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > 20)
                {
                    _stderr.Dequeue();
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new TtsException(TtsFailureKind.NotConfigured, $"No se pudo iniciar Piper: {ex.Message}", ex);
        }

        process.BeginErrorReadLine();
        process.StandardInput.NewLine = "\n";
        _process = process;
        return process;
    }

    private TtsException Crash(string message, Exception? inner = null)
    {
        string tail;
        lock (_stderr)
        {
            tail = string.Join(Environment.NewLine, _stderr.TakeLast(5));
        }

        return new TtsException(TtsFailureKind.EngineCrashed,
            tail.Length == 0 ? message : $"{message}{Environment.NewLine}{tail}", inner);
    }

    private void KillProcess()
    {
        Process? process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Ya terminó.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.Close(); // Piper termina al llegar a fin de entrada
                process.WaitForExit(1500);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        }

        KillProcess();
        _gate.Dispose();
    }
}
