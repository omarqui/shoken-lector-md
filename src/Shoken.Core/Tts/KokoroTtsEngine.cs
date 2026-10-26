using System.Globalization;
using System.Text;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using NumSharp;
using Shoken.Core.Audio;
using Shoken.Core.Caching;
using Shoken.Core.Planning;
using Shoken.Core.Text;

namespace Shoken.Core.Tts;

public sealed class KokoroTtsOptions
{
    /// <summary>Modelo kokoro.onnx (se descarga desde Configuración).</summary>
    public required string ModelPath { get; init; }

    /// <summary>Voz en español: ef_dora, em_alex o em_santa.</summary>
    public required string Voice { get; init; }

    /// <summary>Carpeta con las voces (.npy) que trae la app.</summary>
    public required string VoicesDirectory { get; init; }

    public int RatePercent { get; init; }
}

/// <summary>Lo que el motor necesita de Kokoro. La implementación real usa KokoroSharp y el modelo ONNX.</summary>
public interface IKokoroBackend
{
    int SampleRate { get; }

    /// <summary>Tokens que admite una pasada del modelo.</summary>
    int MaxTokens { get; }

    string Phonemize(string text);

    int[] Tokenize(string phonemes);

    bool IsPunctuation(int token);

    bool IsSpace(int token);

    float[] Infer(int[] tokens, float speed);
}

/// <summary>
/// Motor local con voz natural: Kokoro 82M, en la app (sin programas aparte ni conexión). Fonetiza como en
/// España, así que antes de sintetizar se pasa a español de México (<see cref="KokoroPhonemes.ToMexican"/>).
/// Igual que con Piper, cada segmento se sintetiza aparte y las pausas del Markdown se insertan como silencio.
/// </summary>
public sealed class KokoroTtsEngine : ITtsEngine, IDisposable
{
    /// <summary>Voces en español del modelo: identificador, nombre para mostrar y tipo de voz.</summary>
    public static IReadOnlyList<(string Id, string Name, string Kind)> Voices { get; } =
        [("ef_dora", "Dora", "mujer"), ("em_alex", "Alex", "hombre"), ("em_santa", "Santa", "hombre")];

    // Cambia si cambia cómo se pasan los fonemas a español de México: el audio en caché ya no corresponde.
    private const string PhonemeVersion = "es-MX-1";

    private readonly KokoroTtsOptions _options;
    private readonly SpeechTextNormalizer _normalizer;
    private readonly AudioCache _cache;
    private readonly Func<KokoroTtsOptions, IKokoroBackend> _createBackend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IKokoroBackend? _backend;
    private bool _disposed;

    /// <param name="createBackend">Para las pruebas; por omisión, KokoroSharp con el modelo de <paramref name="options"/>.</param>
    public KokoroTtsEngine(KokoroTtsOptions options, SpeechTextNormalizer normalizer, AudioCache cache,
        Func<KokoroTtsOptions, IKokoroBackend>? createBackend = null)
    {
        _options = options;
        _normalizer = normalizer;
        _cache = cache;
        _createBackend = createBackend ?? (o => new KokoroSharpBackend(o));
    }

    public string Name => $"Kokoro · {Voices.FirstOrDefault(v => v.Id == _options.Voice).Name ?? _options.Voice}";

    private string VoicePath => Path.Combine(_options.VoicesDirectory, _options.Voice + ".npy");

    public bool IsConfigured => File.Exists(_options.ModelPath) && File.Exists(VoicePath);

    private float Speed => (float)Math.Clamp(1 + _options.RatePercent / 100.0, 0.5, 2.0);

    public AudioChunk? TryGetCached(SpeechChunk chunk) =>
        IsConfigured ? _cache.TryGet(Prepare(chunk).Key, chunk, Name) : null;

    public async Task<AudioChunk> SynthesizeAsync(SpeechChunk chunk, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured)
        {
            throw new TtsException(TtsFailureKind.NotConfigured,
                "Kokoro no está instalado: descarga su modelo desde Configuración.");
        }

        (List<string> texts, string key) = Prepare(chunk);
        if (_cache.TryGet(key, chunk, Name) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // La inferencia ocupa el procesador varios segundos: fuera del hilo que pidió el audio.
            return await Task.Run(() => Synthesize(chunk, texts, key, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AudioChunk Synthesize(SpeechChunk chunk, List<string> texts, string key, CancellationToken cancellationToken)
    {
        IKokoroBackend backend;
        try
        {
            backend = _backend ??= _createBackend(_options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TtsException(TtsFailureKind.EngineCrashed, $"No se pudo cargar Kokoro: {ex.Message}", ex);
        }

        var parts = new List<(WavAudio Audio, int Gap)>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WavAudio audio = HasSpeakableText(texts[i])
                ? Speak(backend, texts[i], cancellationToken)
                : WavAudio.Silence(backend.SampleRate, 1, 16, 200);
            parts.Add((audio, chunk.GapBefore(i)));
        }

        WavAudio combined = WavAudio.Concatenate(parts, out IReadOnlyList<TimeSpan> starts);
        var marks = starts.Select((offset, index) => (index, offset)).ToList();
        return _cache.Store(key, combined.ToBytes(), ".wav", marks, chunk, Name, combined.Duration);
    }

    private WavAudio Speak(IKokoroBackend backend, string text, CancellationToken cancellationToken)
    {
        try
        {
            int[] tokens = backend.Tokenize(KokoroPhonemes.ToMexican(backend.Phonemize(text)));
            var samples = new List<float>();
            foreach (int[] piece in KokoroPhonemes.Split(tokens, backend.MaxTokens, backend.IsPunctuation, backend.IsSpace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.AddRange(backend.Infer(piece, Speed));
            }

            return new WavAudio(backend.SampleRate, 1, 16, ToPcm16(samples));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or TtsException))
        {
            throw new TtsException(TtsFailureKind.EngineCrashed, $"Kokoro falló: {ex.Message}", ex);
        }
    }

    private static byte[] ToPcm16(List<float> samples)
    {
        var pcm = new byte[samples.Count * 2];
        for (int i = 0; i < samples.Count; i++)
        {
            short value = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }

        return pcm;
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
            "kokoro",
            _options.Voice,
            modelSize.ToString(CultureInfo.InvariantCulture),
            Speed.ToString(CultureInfo.InvariantCulture),
            PhonemeVersion,
            payload.ToString());
        return (texts, key);
    }

    private static bool HasSpeakableText(string text) => text.Any(char.IsLetterOrDigit);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_backend as IDisposable)?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Kokoro de verdad: KokoroSharp con el modelo ONNX y la voz elegida.</summary>
internal sealed class KokoroSharpBackend : IKokoroBackend, IDisposable
{
    private readonly KokoroModel _model;
    private readonly float[,,] _style;
    private readonly int _space = Tokenizer.Vocab[' '];

    public KokoroSharpBackend(KokoroTtsOptions options)
    {
        _style = LoadVoice(Path.Combine(options.VoicesDirectory, options.Voice + ".npy"));
        _model = new KokoroModel(options.ModelPath, null);
    }

    /// <summary>
    /// Lee la voz solo para lectura. <c>KokoroVoice.FromPath</c> la abre pidiendo también escritura, y empaquetada
    /// (MSIX) la voz está en la carpeta de instalación, que es de solo lectura: Windows le niega el acceso.
    /// </summary>
    internal static float[,,] LoadVoice(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return np.Load<float[,,]>(stream);
    }

    public int SampleRate => 24000;

    // El modelo admite 510 tokens contando los de inicio y fin; se deja margen.
    public int MaxTokens => 500;

    public string Phonemize(string text) =>
        new(Tokenizer.Tokenize(text, "es", preprocess: true).Select(t => Tokenizer.TokenToChar[t]).ToArray());

    public int[] Tokenize(string phonemes) => Tokenizer.TokenizePhonemes(phonemes.ToCharArray());

    public bool IsPunctuation(int token) => Tokenizer.PunctuationTokens.Contains(token);

    public bool IsSpace(int token) => token == _space;

    public float[] Infer(int[] tokens, float speed) => _model.Infer(tokens, _style, speed);

    public void Dispose() => _model.Dispose();
}
