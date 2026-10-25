using System.IO;
using System.Windows.Threading;
using Shoken.App.Services;
using Shoken.Core.Coverage;
using Shoken.Core.Model;

namespace Shoken.App;

/// <summary>
/// Lo ya escuchado de cada documento: el reproductor avisa qué segmentos sonaron completos, aquí se juntan por bloque,
/// se guardan (uno por documento, en %LOCALAPPDATA%\Shoken\escuchado) y se muestran tenues en el margen del documento,
/// en el índice y en la línea de tiempo. Solo los archivos: un texto pegado no guarda lo escuchado.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan CoverageSaveDelay = TimeSpan.FromSeconds(2);

    private CoverageStore? _coverageStore;
    private CoverageContext? _coverage;
    private IReadOnlySet<int> _heardSegments = new HashSet<int>(); // los segmentos de los bloques escuchados (se arma al cambiar)
    private bool _coverageDirty; // hay algo escuchado que todavía no se guardó
    private DispatcherTimer? _coverageSaveTimer;

    /// <summary>Lo escuchado del documento de la pestaña activa, con la ruta con que se guarda.</summary>
    private sealed record CoverageContext(DocumentTab Tab, string Path, SpeechDocument Document, DocumentCoverage Coverage);

    private CoverageStore Coverage => _coverageStore ??= new CoverageStore(_paths.HeardDirectory);

    /// <summary>
    /// Al cargar un documento, antes de armar la línea de tiempo y el índice: lo que ya se había escuchado. Un bloque
    /// que se editó desde entonces ya no cuenta (se reconoce por su texto).
    /// </summary>
    private void StartCoverage(DocumentTab tab, SpeechDocument document)
    {
        StopCoverage();
        if (!tab.IsDemo && !tab.State.IsEmpty && !tab.State.IsPasted)
        {
            string path = tab.State.Path;
            IReadOnlyList<HeardBlock> stored;
            try
            {
                stored = Coverage.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(ex);
                stored = [];
            }

            _coverage = new CoverageContext(tab, path, document, new DocumentCoverage(document, stored));
        }

        _heardSegments = _coverage?.Coverage.HeardSegments ?? new HashSet<int>();
    }

    /// <summary>Al dejar de mostrar el documento (otra pestaña, cerrarla, ninguna): se guarda lo pendiente y se suelta.</summary>
    private void StopCoverage()
    {
        SaveCoverageNow();
        _coverage = null;
        _heardSegments = new HashSet<int>();
    }

    /// <summary>Pinta en la página lo escuchado del documento recién mostrado.</summary>
    private Task ShowHeardAsync() =>
        _coverage is { Coverage.HeardAnchors.Count: > 0 } context && _view is not null
            ? _view.SetHeardAsync(context.Coverage.HeardAnchors)
            : Task.CompletedTask;

    private void OnSegmentHeard(int segmentId)
    {
        if (_coverage is not { } context || context.Document != _document || !context.Coverage.MarkSegmentHeard(segmentId))
        {
            return;
        }

        UpdateCoverageView(context, segmentId);
        _coverageDirty = true;
        // Como mucho 2 s después de lo primero sin guardar: si la app se cierra de golpe, se pierde poco.
        _coverageSaveTimer ??= CreateCoverageSaveTimer();
        if (!_coverageSaveTimer.IsEnabled)
        {
            _coverageSaveTimer.Start();
        }
    }

    /// <summary>Página, índice y línea de tiempo al día con lo escuchado.</summary>
    /// <param name="heardSegmentId">
    /// El segmento con que un bloque quedó escuchado: en el índice solo cambian, en su sitio, las filas de su capítulo.
    /// Null para rehacer el índice entero («Olvidar lo escuchado»).
    /// </param>
    private void UpdateCoverageView(CoverageContext context, int? heardSegmentId = null)
    {
        _heardSegments = context.Coverage.HeardSegments;
        _ = _view?.SetHeardAsync(context.Coverage.HeardAnchors);
        if (heardSegmentId is { } segmentId)
        {
            UpdateOutlineHeard(segmentId);
        }
        else
        {
            RefreshOutline();
        }

        Player.SetHeard(HeardSpans());
    }

    /// <summary>
    /// Sin tocar la lista del índice (que cerraría los tooltips, quitaría el ▶ del hover y volvería a anunciar la fila
    /// con el foco): solo la fracción escuchada de las filas que suman el capítulo del segmento. Un bloque nunca cruza
    /// un título, así que todo él está en ese capítulo.
    /// </summary>
    private void UpdateOutlineHeard(int segmentId)
    {
        if (_timeline is not { Chapters.Count: > 0 } timeline || OutlineList.ItemsSource is not IEnumerable<OutlineItem> rows)
        {
            return;
        }

        TimelineChapter chapter = timeline.Chapters[timeline.ChapterIndexOf(segmentId)];
        foreach (OutlineItem row in rows)
        {
            if (row.Chapters.Contains(chapter))
            {
                row.HeardFraction = HeardFraction(row.Chapters);
            }
        }
    }

    /// <summary>Los tramos escuchados de la línea de tiempo (se vuelven a calcular con cada línea de tiempo nueva).</summary>
    private IReadOnlyList<TimelineSpan> HeardSpans() =>
        _timeline is { } timeline && _heardSegments.Count > 0 ? timeline.SpansOf(_heardSegments) : [];

    /// <summary>
    /// Cuánto de una fila del índice se escuchó (0 a 1), sobre los mismos capítulos con que se calcula su duración. Los
    /// segmentos de un capítulo van de su primero al primero del capítulo siguiente (o al final del documento).
    /// </summary>
    private double HeardFraction(IReadOnlyList<TimelineChapter> chapters)
    {
        if (_timeline is not { } timeline || _document is null || _heardSegments.Count == 0)
        {
            return 0;
        }

        TimeSpan total = TimeSpan.Zero;
        TimeSpan heard = TimeSpan.Zero;
        foreach (TimelineChapter chapter in chapters)
        {
            // El índice de capítulo de cada segmento ya viene armado con la línea de tiempo: no se busca en cada fila.
            int end = timeline.ChapterEnd(timeline.ChapterIndexOf(chapter.FirstSegmentId));
            total += chapter.Duration;
            heard += timeline.HeardDuration(_heardSegments, chapter.FirstSegmentId, end);
        }

        return total > TimeSpan.Zero ? Math.Clamp(heard / total, 0, 1) : 0;
    }

    /// <summary>«Olvidar lo escuchado» del menú de la pestaña: el documento vuelve a quedar sin escuchar.</summary>
    private void ForgetHeard(DocumentTab tab)
    {
        if (_coverage is { } context && context.Tab == tab)
        {
            _coverageSaveTimer?.Stop();
            _coverageDirty = false;
            context.Coverage.Clear();
            UpdateCoverageView(context);
        }

        try
        {
            Coverage.Delete(tab.State.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo borrar lo escuchado.", ok: false);
            return;
        }

        ShowToast("Se olvidó lo escuchado");
    }

    /// <summary>Guarda ya lo pendiente (al cambiar o cerrar la pestaña, y al salir de la app).</summary>
    private void SaveCoverageNow()
    {
        _coverageSaveTimer?.Stop();
        if (_coverage is not { } context || !_coverageDirty)
        {
            return;
        }

        _coverageDirty = false;
        try
        {
            Coverage.Save(context.Path, context.Coverage.ToStored());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex); // es un detalle de fondo: no vale un aviso
        }
    }

    private DispatcherTimer CreateCoverageSaveTimer()
    {
        var timer = new DispatcherTimer { Interval = CoverageSaveDelay };
        timer.Tick += (_, _) => SaveCoverageNow();
        return timer;
    }
}
