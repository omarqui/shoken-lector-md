using System.Windows.Media;
using System.Windows.Threading;
using LectorMd.Core.Model;
using LectorMd.Core.Planning;
using LectorMd.Core.Tts;

namespace LectorMd.App.Services;

public enum PlaybackState
{
    Stopped,
    Loading,
    Playing,
    Paused,
}

/// <summary>
/// Reproduce el documento bloque por bloque: pide el audio al motor (con 2 bloques de anticipación),
/// lo reproduce con <see cref="MediaPlayer"/> y avisa qué segmento suena según las marcas de tiempo.
/// Todo se usa desde el hilo de la interfaz. Cada lectura es una "sesión": al detener o saltar se cancela
/// y su bucle deja de tocar el reproductor aunque alguna continuación ya estuviera en cola.
/// </summary>
internal sealed class ReadAloudController : IDisposable
{
    private const int PrefetchAhead = 2;
    private static readonly TimeSpan HighlightLead = TimeSpan.FromMilliseconds(60);

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;

    private ITtsEngine? _engine;
    private SpeechDocument? _document;
    private IReadOnlyList<SpeechChunk> _chunks = [];
    private int[] _chunkOfSegment = [];
    private CancellationTokenSource? _session;
    private TaskCompletionSource? _ended;
    private AudioChunk? _current;
    private int _currentSegmentId = -1;
    private int? _resumeSegmentId;
    private double _speed = 1.0;
    private bool _mediaReady;    // hay un bloque abierto y posicionado
    private bool _pausePending;  // se pidió pausa mientras se preparaba el audio

    public ReadAloudController()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => UpdateCurrentSegment();
        _player.MediaEnded += (_, _) => _ended?.TrySetResult();
        _player.MediaFailed += (_, e) => _ended?.TrySetException(
            e.ErrorException ?? new InvalidOperationException("No se pudo reproducir el audio."));
    }

    public event Action<SpeechSegment>? SegmentChanged;

    public event Action<PlaybackState>? StateChanged;

    public event Action<AudioChunk>? ChunkStarted;

    public event Action? DocumentFinished;

    public event Action<Exception>? Failed;

    public PlaybackState State { get; private set; }

    public bool HasDocument => _document is not null && _chunks.Count > 0;

    /// <summary>Segmento que suena ahora, o el último que sonó si está detenido.</summary>
    public int CurrentSegmentId => _currentSegmentId >= 0 ? _currentSegmentId : _resumeSegmentId ?? -1;

    /// <summary>Velocidad de reproducción (no re-sintetiza ni gasta cuota).</summary>
    public double PlaybackSpeed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0.5, 2.0);
            if (State is PlaybackState.Playing or PlaybackState.Paused)
            {
                _player.SpeedRatio = _speed;
            }
        }
    }

    public void SetEngine(ITtsEngine engine)
    {
        Stop();
        _engine = engine;
    }

    public void Load(SpeechDocument document, IReadOnlyList<SpeechChunk> chunks, int? resumeSegmentId = null)
    {
        Stop();
        _document = document;
        _chunks = chunks;
        _chunkOfSegment = ChunkPlanner.BuildSegmentIndex(chunks, document.Segments.Count);
        _currentSegmentId = -1;
        _resumeSegmentId = resumeSegmentId is { } id && id >= 0 && id < document.Segments.Count ? id : null;
    }

    /// <summary>Detiene y olvida el documento (al cerrar su pestaña o pasar a una vacía).</summary>
    public void Unload()
    {
        Stop();
        _document = null;
        _chunks = [];
        _chunkOfSegment = [];
        _resumeSegmentId = null;
    }

    public void TogglePlayPause()
    {
        switch (State)
        {
            case PlaybackState.Playing:
            case PlaybackState.Loading:
                Pause();
                break;
            case PlaybackState.Paused:
                Resume();
                break;
            default:
                PlayFrom(_resumeSegmentId ?? 0);
                break;
        }
    }

    public void Pause()
    {
        if (State == PlaybackState.Playing)
        {
            _player.Pause();
            _timer.Stop();
            // Si justo terminó un bloque y se está abriendo el siguiente, que tampoco arranque.
            _pausePending = !_mediaReady;
            SetState(PlaybackState.Paused);
        }
        else if (State == PlaybackState.Loading)
        {
            // El audio todavía se está generando: cuando llegue, se queda en pausa.
            _pausePending = true;
            SetState(PlaybackState.Paused);
        }
    }

    public void Resume()
    {
        if (State != PlaybackState.Paused)
        {
            return;
        }

        if (_pausePending && !_mediaReady)
        {
            _pausePending = false;
            SetState(PlaybackState.Loading); // sigue esperando el audio y luego reproduce
            return;
        }

        _pausePending = false;
        _player.Play();
        _player.SpeedRatio = _speed;
        _timer.Start();
        SetState(PlaybackState.Playing);
    }

    public void Stop()
    {
        _session?.Cancel();
        _session = null;
        _ended?.TrySetCanceled();
        _ended = null;
        _timer.Stop();
        _player.Stop();
        _player.Close();
        _current = null;
        _mediaReady = false;
        _pausePending = false;
        if (_currentSegmentId >= 0)
        {
            _resumeSegmentId = _currentSegmentId;
        }

        _currentSegmentId = -1;
        SetState(PlaybackState.Stopped);
    }

    public void PlayFrom(int segmentId)
    {
        if (_document is null || _engine is null || _chunks.Count == 0)
        {
            return;
        }

        _ = RunAsync(_engine, Math.Clamp(segmentId, 0, _document.Segments.Count - 1));
    }

    public void NextSection()
    {
        if (_document is null)
        {
            return;
        }

        int from = CurrentSegmentId;
        SpeechSegment? next = _document.Segments.FirstOrDefault(s => s.IsHeading && s.Id > from);
        if (next is not null)
        {
            PlayFrom(next.Id);
        }
    }

    public void PreviousSection()
    {
        if (_document is null)
        {
            return;
        }

        int from = Math.Max(0, CurrentSegmentId);
        SpeechSegment? section = _document.SectionOf(from);
        int target;
        if (section is null)
        {
            target = 0;
        }
        else if (from - section.Id <= 1)
        {
            // Recién empezó esta sección: ir a la anterior.
            target = _document.Segments.LastOrDefault(s => s.IsHeading && s.Id < section.Id)?.Id ?? 0;
        }
        else
        {
            target = section.Id; // volver al inicio de la sección actual
        }

        PlayFrom(target);
    }

    private async Task RunAsync(ITtsEngine engine, int startSegmentId)
    {
        Stop();
        var session = new CancellationTokenSource();
        _session = session;
        CancellationToken token = session.Token;
        var pending = new Dictionary<int, Task<AudioChunk>>(); // propio de esta sesión
        SetState(PlaybackState.Loading);

        try
        {
            bool first = true;
            for (int index = _chunkOfSegment[startSegmentId]; index < _chunks.Count; index++)
            {
                Task<AudioChunk> next = GetOrStart(engine, pending, index, token);
                if (!next.IsCompleted && State == PlaybackState.Playing)
                {
                    SetState(PlaybackState.Loading); // el siguiente bloque aún no está listo
                }

                AudioChunk audio = await next.WaitAsync(token);
                token.ThrowIfCancellationRequested();

                for (int ahead = 1; ahead <= PrefetchAhead && index + ahead < _chunks.Count; ahead++)
                {
                    GetOrStart(engine, pending, index + ahead, token);
                }

                TimeSpan startAt = first ? audio.OffsetOf(startSegmentId) : TimeSpan.Zero;
                first = false;
                await PlayChunkAsync(audio, startAt, token);
                pending.Remove(index);
            }

            token.ThrowIfCancellationRequested();
            Finish();
        }
        catch (OperationCanceledException)
        {
            // Se detuvo o se saltó a otra parte.
        }
        catch (Exception ex) when (_session == session)
        {
            Stop();
            Failed?.Invoke(ex);
        }
        catch (Exception)
        {
            // Error de una sesión ya reemplazada: no interesa.
        }
    }

    private Task<AudioChunk> GetOrStart(ITtsEngine engine, Dictionary<int, Task<AudioChunk>> pending, int index,
        CancellationToken token)
    {
        if (pending.TryGetValue(index, out Task<AudioChunk>? existing))
        {
            return existing;
        }

        SpeechChunk chunk = _chunks[index];
        Task<AudioChunk> task;
        if (engine.TryGetCached(chunk) is { } cached)
        {
            task = Task.FromResult(cached);
        }
        else
        {
            task = Task.Run(() => engine.SynthesizeAsync(chunk, token), token);
            // Los errores de bloques adelantados se ven cuando se piden; aquí solo se marcan como observados.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        pending[index] = task;
        return task;
    }

    private async Task PlayChunkAsync(AudioChunk audio, TimeSpan startAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _current = audio;
        _mediaReady = false;
        ChunkStarted?.Invoke(audio);

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, EventArgs e) => opened.TrySetResult();
        void OnFailed(object? sender, ExceptionEventArgs e) => opened.TrySetException(e.ErrorException);

        _player.MediaOpened += OnOpened;
        _player.MediaFailed += OnFailed;
        try
        {
            _player.Open(new Uri(audio.FilePath, UriKind.Absolute));
            using (token.Register(() => opened.TrySetCanceled()))
            {
                await opened.Task;
            }
        }
        finally
        {
            _player.MediaOpened -= OnOpened;
            _player.MediaFailed -= OnFailed;
        }

        token.ThrowIfCancellationRequested();

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ended = ended;
        if (startAt > TimeSpan.Zero)
        {
            _player.Position = startAt;
        }

        _mediaReady = true;
        UpdateCurrentSegment(startAt);
        if (_pausePending)
        {
            SetState(PlaybackState.Paused); // Resume() iniciará la reproducción
        }
        else
        {
            _player.Play();
            _player.SpeedRatio = _speed;
            _timer.Start();
            SetState(PlaybackState.Playing);
        }

        using (token.Register(() => ended.TrySetCanceled()))
        {
            await ended.Task;
        }

        token.ThrowIfCancellationRequested();
        _timer.Stop();
        _mediaReady = false;
        if (_ended == ended)
        {
            _ended = null;
        }
    }

    private void UpdateCurrentSegment() => UpdateCurrentSegment(_player.Position);

    private void UpdateCurrentSegment(TimeSpan position)
    {
        if (_current is null || _document is null)
        {
            return;
        }

        int id = _current.SegmentAt(position + HighlightLead);
        if (id >= 0 && id < _document.Segments.Count && id != _currentSegmentId)
        {
            _currentSegmentId = id;
            SegmentChanged?.Invoke(_document.Segments[id]);
        }
    }

    private void Finish()
    {
        _timer.Stop();
        _player.Close();
        _current = null;
        _mediaReady = false;
        _pausePending = false;
        _session = null;
        _currentSegmentId = -1;
        _resumeSegmentId = null;
        SetState(PlaybackState.Stopped);
        DocumentFinished?.Invoke();
    }

    private void SetState(PlaybackState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        Stop();
        _player.Close();
    }
}
