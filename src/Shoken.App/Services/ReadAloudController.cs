using System.Windows.Media;
using System.Windows.Threading;
using Shoken.Core.Audio;
using Shoken.Core.Coverage;
using Shoken.Core.Model;
using Shoken.Core.Planning;
using Shoken.Core.Tts;

namespace Shoken.App.Services;

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
    private const int ProgressIntervalMs = 250;
    private static readonly TimeSpan HighlightLead = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MinimumStart = TimeSpan.FromMilliseconds(1);

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private readonly HeardTracker _heard = new();

    private ITtsEngine? _engine;
    private SpeechDocument? _document;
    private IReadOnlyList<SpeechChunk> _chunks = [];
    private int[] _chunkOfSegment = [];
    private CancellationTokenSource? _session;
    private TaskCompletionSource? _ended;
    private AudioChunk? _current;
    private int _currentSegmentId = -1;
    private WordTimeline? _words;  // tiempos por palabra del bloque abierto
    private int _currentWord = -1;
    private int? _resumeSegmentId;
    private double _speed = 1.0;
    private bool _mediaReady;    // hay un bloque abierto y posicionado
    private bool _pausePending;  // se pidió pausa mientras se preparaba el audio
    private long _lastProgressTicks;
    private int _diagnosticSamples; // muestras de posición que faltan registrar (solo con LECTORMD_DIAGNOSTICO=1)
    private TimeSpan _positionFloor; // antes de esta posición del bloque se muestra el párrafo pedido (pre-arranque)
    private bool _seeking; // −10 s / +10 s (dentro del bloque o al caer en otro): el cambio de párrafo no es avance de la lectura

    /// <summary>Cuánto antes del párrafo pedido arrancar (solo en Escritorio remoto; 0 en el equipo local).</summary>
    public TimeSpan PreRoll { get; set; }

    public ReadAloudController()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => UpdateCurrentSegment();
        _player.MediaEnded += (_, _) => _ended?.TrySetResult();
        _player.MediaFailed += (_, e) => _ended?.TrySetException(
            e.ErrorException ?? new InvalidOperationException("No se pudo reproducir el audio."));
        _heard.SegmentHeard += id => SegmentHeard?.Invoke(id);
    }

    public event Action<SpeechSegment>? SegmentChanged;

    /// <summary>
    /// Cambió la palabra que suena: índice en <see cref="WordTimeline.Tokenize(SpeechSegment)"/> del segmento, o -1
    /// si todavía no empieza ninguna. Llega después de <see cref="SegmentChanged"/>.
    /// </summary>
    public event Action<SpeechSegment, int>? WordChanged;

    /// <summary>Un segmento sonó completo, de principio a fin y sin saltos (ver <see cref="HeardTracker"/>).</summary>
    public event Action<int>? SegmentHeard;

    public event Action<PlaybackState>? StateChanged;

    public event Action<AudioChunk>? ChunkStarted;

    public event Action? DocumentFinished;

    public event Action<Exception>? Failed;

    /// <summary>La salida de audio desapareció; la lectura quedó en pausa en el párrafo actual.</summary>
    public event Action? AudioDeviceLost;

    /// <summary>Avance dentro del segmento que suena (segmento, tiempo desde que empezó), unas 4 veces por segundo.</summary>
    public event Action<int, TimeSpan>? ProgressChanged;

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

        if (_session is null)
        {
            // Pausa sin audio abierto (se perdió la salida de audio): se reanuda desde el párrafo guardado.
            PlayFrom(_resumeSegmentId ?? 0);
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
        _words = null;
        _currentWord = -1;
        _mediaReady = false;
        _pausePending = false;
        if (_currentSegmentId >= 0)
        {
            _resumeSegmentId = _currentSegmentId;
        }

        _currentSegmentId = -1;
        _heard.Reset();
        SetState(PlaybackState.Stopped);
    }

    /// <summary>
    /// Mueve la posición dentro del bloque que ya está abierto, sin volver a abrirlo (y sin cortar si está en
    /// pausa). False si el destino cae fuera del bloque: entonces hay que usar <see cref="PlayFrom"/>.
    /// </summary>
    public bool TrySeekWithinCurrent(TimeSpan delta)
    {
        if (_current is null || !_mediaReady || !_player.NaturalDuration.HasTimeSpan)
        {
            return false;
        }

        TimeSpan target = _player.Position + delta;
        if (target < TimeSpan.Zero || target >= _player.NaturalDuration.TimeSpan)
        {
            return false;
        }

        _player.Position = target;
        _positionFloor = TimeSpan.Zero; // el usuario se movió: ya no hay pre-arranque que ocultar
        _lastProgressTicks = 0; // que la línea de tiempo se actualice ya, aunque esté en pausa
        _seeking = true;
        try
        {
            UpdateCurrentSegment(target);
        }
        finally
        {
            _seeking = false;
        }

        _heard.Jumped(_currentSegmentId); // el párrafo donde cayó no sonó desde su principio
        return true;
    }

    /// <param name="intoSegment">Cuánto saltar dentro del párrafo (para retroceder o adelantar 10 s).</param>
    public void PlayFrom(int segmentId, TimeSpan intoSegment = default)
    {
        if (_document is null || _engine is null || _chunks.Count == 0)
        {
            return;
        }

        PlaybackDiagnostics.Restart($"leer desde el segmento {segmentId} (+{intoSegment.TotalSeconds:0.00} s)");
        _ = RunAsync(_engine, Math.Clamp(segmentId, 0, _document.Segments.Count - 1), intoSegment);
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

    private async Task RunAsync(ITtsEngine engine, int startSegmentId, TimeSpan intoSegment = default)
    {
        Stop();
        _heard.Started(startSegmentId, intoSegment <= TimeSpan.Zero); // a mitad del párrafo (−10 s / +10 s), ese no cuenta
        _resumeSegmentId = startSegmentId; // mientras carga, CurrentSegmentId es el párrafo pedido
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
                PlaybackDiagnostics.Log($"bloque {index} listo ({(audio.FromCache ? "de la caché" : "recién generado")}, {audio.EngineName})");
                token.ThrowIfCancellationRequested();

                for (int ahead = 1; ahead <= PrefetchAhead && index + ahead < _chunks.Count; ahead++)
                {
                    GetOrStart(engine, pending, index + ahead, token);
                }

                TimeSpan startAt = first ? audio.OffsetOf(startSegmentId) + intoSegment : TimeSpan.Zero;
                // En Escritorio remoto, RDP descarta el primer tramo de audio después de un silencio: se arranca un
                // poco antes (normalmente dentro de la pausa previa al párrafo) y lo descartado es silencio.
                _positionFloor = startAt;
                if (first && PreRoll > TimeSpan.Zero && startAt > TimeSpan.Zero)
                {
                    startAt = startAt > PreRoll ? startAt - PreRoll : TimeSpan.Zero;
                }

                // A mitad del párrafo (−10 s / +10 s), el punto sale de duraciones estimadas: puede caer ya en el
                // siguiente párrafo, que tampoco sonó desde su principio.
                bool landing = first && intoSegment > TimeSpan.Zero;
                first = false;
                await PlayChunkAsync(audio, _chunks[index], startAt, landing, token);
                pending.Remove(index);
            }

            token.ThrowIfCancellationRequested();
            Finish();
        }
        catch (OperationCanceledException)
        {
            // Se detuvo o se saltó a otra parte.
        }
        catch (Exception ex) when (_session == session && MediaErrors.IsAudioDeviceLost(ex))
        {
            // No es una falla: queda en pausa en el párrafo actual y Resume() vuelve a empezar desde ahí.
            Stop();
            SetState(PlaybackState.Paused);
            AudioDeviceLost?.Invoke();
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

    /// <param name="landing">True si la lectura cae a mitad de un párrafo: el primer cambio de párrafo es un salto.</param>
    private async Task PlayChunkAsync(AudioChunk audio, SpeechChunk chunk, TimeSpan startAt, bool landing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _current = audio;
        _words = null;
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
        PlaybackDiagnostics.Log($"archivo abierto: dura {(_player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : -1):0.00} s, " +
                                $"se pide empezar en {startAt.TotalSeconds:0.000} s");
        // Nunca desde 0 exacto: si después se le cambia la velocidad (SpeedRatio distinto de 1), MediaPlayer salta de
        // 1 a 5 s hacia adelante y se pierde el inicio del bloque (medido con MP3 de Azure a 1.25x y 1.5x). Desde 1 ms
        // no pasa.
        _player.Position = startAt > MinimumStart ? startAt : MinimumStart;
        _words = WordTimeline.Build(chunk, audio, _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : null);

        _diagnosticSamples = PlaybackDiagnostics.Enabled ? 15 : 0;

        _mediaReady = true;
        if (landing)
        {
            _seeking = true;
            try
            {
                UpdateCurrentSegment(startAt);
            }
            finally
            {
                _seeking = false;
            }

            _heard.Jumped(_currentSegmentId); // el párrafo donde cayó no sonó desde su principio
        }
        else
        {
            UpdateCurrentSegment(startAt);
        }

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

        // Durante el pre-arranque la posición cae en el párrafo anterior: el resaltado se queda en el pedido.
        if (position < _positionFloor)
        {
            position = _positionFloor;
        }

        if (_diagnosticSamples > 0)
        {
            _diagnosticSamples--;
            PlaybackDiagnostics.Log($"reproduciendo: posición real {_player.Position.TotalSeconds:0.000} s ({State})");
        }

        int id = _current.SegmentAt(position + HighlightLead);
        bool segmentChanged = false;
        if (id >= 0 && id < _document.Segments.Count && id != _currentSegmentId)
        {
            _currentSegmentId = id;
            segmentChanged = true;
            if (!_seeking)
            {
                _heard.Moved(id);
            }

            SegmentChanged?.Invoke(_document.Segments[id]);
        }

        if (_words is not null && _currentSegmentId >= 0)
        {
            int word = _words.WordAt(_currentSegmentId, position + HighlightLead);
            if (segmentChanged || word != _currentWord)
            {
                _currentWord = word;
                WordChanged?.Invoke(_document.Segments[_currentSegmentId], word);
            }
        }

        // La línea de tiempo no necesita 60 actualizaciones por segundo.
        long now = Environment.TickCount64;
        if (_currentSegmentId >= 0 && now - _lastProgressTicks >= ProgressIntervalMs)
        {
            _lastProgressTicks = now;
            ProgressChanged?.Invoke(_currentSegmentId, position - _current.OffsetOf(_currentSegmentId));
        }
    }

    private void Finish()
    {
        _timer.Stop();
        _player.Close();
        _current = null;
        _words = null;
        _currentWord = -1;
        _mediaReady = false;
        _pausePending = false;
        _session = null;
        _heard.Finished(); // el último párrafo también sonó completo
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
