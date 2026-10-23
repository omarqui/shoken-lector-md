using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core.Model;
using LectorMd.Core.Settings;
using LectorMd.Core.Usage;

namespace LectorMd.App.Controls;

/// <summary>
/// Reproductor de abajo: línea de tiempo por capítulos, transporte, sección y párrafo, voz, velocidad y consumo.
/// Solo muestra lo que le dicen y avisa lo que se pulsa; la lógica vive en la ventana.
/// </summary>
public partial class PlayerBar : UserControl
{
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(8);
    private static readonly CultureInfo Mexico = CultureInfo.GetCultureInfo("es-MX");

    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _statusCloseTimer;
    private string? _usagePercent;
    private readonly List<(Border Fill, TextBlock Label, TimelineChapter Chapter)> _cells = [];
    private readonly List<(Button Button, double Speed)> _speedButtons = [];
    private ReadingTimeline? _timeline;
    private string _detail = "";
    private TimeSpan _lastElapsed;
    private int _lastChapter = -1;
    private bool _noticeShowing;

    public PlayerBar()
    {
        InitializeComponent();
        _noticeTimer = new DispatcherTimer { Interval = NoticeTime };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _noticeShowing = false;
            ShowDetail(_detail);
        };

        _statusCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusCloseTimer.Tick += (_, _) =>
        {
            _statusCloseTimer.Stop();
            if (!StatusButton.IsMouseOver && !StatusPanel.IsMouseOver)
            {
                StatusPopup.IsOpen = false;
            }
        };
        StatusPopup.CustomPopupPlacementCallback = PlaceStatusPanel;

        foreach (double speed in PlaybackSpeeds.Steps)
        {
            var button = new Button
            {
                Style = (Style)Resources["BareButton"],
                Content = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×",
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
            };
            button.SetResourceReference(FontSizeProperty, "LectorFontSize13"); // sigue el tamaño del texto (Configuración)
            AutomationProperties.SetName(button, "Velocidad " + button.Content);
            button.Click += (_, _) => SpeedChanged?.Invoke(speed);
            SpeedPanel.Children.Add(button);
            _speedButtons.Add((button, speed));
        }

        SetTimeline(null);
    }

    public event Action? PlayPauseClicked;
    public event Action? PreviousClicked;
    public event Action? NextClicked;
    public event Action<double>? SpeedChanged;
    public event Action? RefreshUsageClicked;

    /// <summary>−10 s o +10 s.</summary>
    public event Action<TimeSpan>? SkipRequested;

    /// <summary>Se eligió otro motor en el panel del estado.</summary>
    public event Action<EngineChoice>? EngineChoiceRequested;

    /// <summary>Clic en un tramo de la línea de tiempo: el ancla del título (null = desde el principio).</summary>
    public event Action<string?>? ChapterClicked;

    /// <summary>Arma la línea de tiempo; null = no hay documento (pestaña vacía o bienvenida).</summary>
    /// <param name="keepProgress">
    /// True al corregir duraciones del mismo documento: se conserva el avance mostrado en vez de volver a 00:00
    /// (si no, el tiempo retrocede un instante en cada bloque nuevo).
    /// </param>
    public void SetTimeline(ReadingTimeline? timeline, bool keepProgress = false)
    {
        _timeline = timeline;
        _cells.Clear();
        TimelineGrid.Children.Clear();
        TimelineGrid.ColumnDefinitions.Clear();
        if (timeline is null)
        {
            ElapsedText.Text = "--:--";
            RemainingText.Text = "--:--";
            SectionText.Text = "Abre o pega un documento";
            _detail = "";
            ShowDetail(_noticeShowing ? DetailText.Text : "");
            return;
        }

        for (int i = 0; i < timeline.Chapters.Count; i++)
        {
            TimelineChapter chapter = timeline.Chapters[i];
            // Hasta un capítulo vacío se ve un poco, para poder pulsarlo.
            TimelineGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(chapter.Duration.TotalSeconds, timeline.Total.TotalSeconds / 60 + 0.1), GridUnitType.Star),
            });

            var fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0, 1),
            };
            fill.SetResourceReference(Border.BackgroundProperty, "LectorAccentBrush");
            var track = new Grid { Height = 6 };
            var trackBack = new Border { CornerRadius = new CornerRadius(3) };
            trackBack.SetResourceReference(Border.BackgroundProperty, "LectorTrackBrush");
            track.Children.Add(trackBack);
            track.Children.Add(fill);

            var label = new TextBlock { Text = chapter.Title, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LectorText2Brush");
            label.SetResourceReference(TextBlock.FontSizeProperty, "LectorFontSize11");

            var content = new StackPanel();
            content.Children.Add(track);
            content.Children.Add(label);

            string duration = Format(chapter.Duration) + (chapter.IsEstimated ? " (estimado)" : "");
            var cell = new Button
            {
                Style = (Style)Resources["BareButton"],
                Content = content,
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = $"{chapter.Title} · {duration} · clic para leer desde aquí",
            };
            AutomationProperties.SetName(cell, $"{chapter.Title}, {duration}");
            string? anchor = chapter.AnchorId;
            cell.Click += (_, _) => ChapterClicked?.Invoke(anchor);
            Grid.SetColumn(cell, i);
            TimelineGrid.Children.Add(cell);
            _cells.Add((fill, label, chapter));
        }

        SetProgress(keepProgress ? _lastElapsed : TimeSpan.Zero, keepProgress ? _lastChapter : -1);
    }

    /// <summary>Rellena lo escuchado hasta <paramref name="elapsed"/> y marca el capítulo actual.</summary>
    public void SetProgress(TimeSpan elapsed, int chapterIndex)
    {
        _lastElapsed = elapsed;
        _lastChapter = chapterIndex;
        if (_timeline is null)
        {
            return;
        }

        for (int i = 0; i < _cells.Count; i++)
        {
            (Border fill, TextBlock label, TimelineChapter chapter) = _cells[i];
            double fraction = chapter.Duration <= TimeSpan.Zero
                ? (elapsed >= chapter.Start + chapter.Duration && i < chapterIndex ? 1 : 0)
                : Math.Clamp((elapsed - chapter.Start) / chapter.Duration, 0, 1);
            ((ScaleTransform)fill.RenderTransform).ScaleX = fraction;
            label.SetResourceReference(TextBlock.ForegroundProperty, i == chapterIndex ? "LectorTextBrush" : "LectorText2Brush");
            label.FontWeight = i == chapterIndex ? FontWeights.SemiBold : FontWeights.Normal;
        }

        bool estimated = _timeline.Chapters.Any(c => c.IsEstimated);
        TimeSpan remaining = _timeline.Total - elapsed;
        ElapsedText.Text = Format(elapsed);
        RemainingText.Text = "−" + (estimated ? "≈" : "") + Format(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
    }

    /// <summary>Sección actual y su detalle ("Párrafo 4 de 12 · capítulo 2 de 5").</summary>
    public void SetPosition(string section, string detail)
    {
        SectionText.Text = section;
        _detail = detail;
        if (!_noticeShowing)
        {
            ShowDetail(detail);
        }
    }

    /// <summary>Lo que muestra ahora (para pasarlo al mini reproductor).</summary>
    public string CurrentSection => SectionText.Text;

    public string CurrentDetail => DetailText.Text;

    /// <summary>
    /// Quita el aviso y vuelve al detalle de la posición. Con <paramref name="onlyIf"/>, solo si el aviso es ese
    /// (así "Preparando el audio…" se va al sonar, pero "Sigo con Piper" se queda sus 8 segundos).
    /// </summary>
    public void ClearNotice(string? onlyIf = null)
    {
        if (onlyIf is not null && DetailText.Text != onlyIf)
        {
            return;
        }

        _noticeTimer.Stop();
        _noticeShowing = false;
        ShowDetail(_detail);
    }

    /// <summary>Cierra el panel del estado (al cambiar de app u ocultar la ventana: es "siempre visible").</summary>
    public void ClosePopup() => StatusPopup.IsOpen = false;

    public bool IsPopupOpen => StatusPopup.IsOpen;

    /// <summary>Un aviso (antes iba en la barra de estado): se queda 8 s sobre el detalle de la posición.</summary>
    public void ShowNotice(string text)
    {
        _noticeShowing = true;
        ShowDetail(text);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    /// <param name="name">Voz y motor ("Dalia · Azure").</param>
    /// <param name="detail">De dónde sale el audio o el motivo del respaldo.</param>
    public void SetEngine(string name, string detail, Brush dot, string? reason)
    {
        EngineDot.Fill = dot;
        PopupDot.Fill = dot;
        PopupEngine.Text = name;
        PopupEngineDetail.Text = reason is null || detail.Contains(reason, StringComparison.Ordinal) ? detail : $"{detail}\nMotivo: {reason}";
        StatusLabel.Text = name;
        UpdateStatusName();
    }

    /// <param name="visible">False si no hay Azure configurado o se usa solo Piper: el componente muestra solo el motor.</param>
    /// <param name="azureReport">"Azure Monitor: 23 060 a las 01:01", o null si todavía no se consultó.</param>
    public void SetUsage(bool visible, int used, int limit, DateTime resetsOnUtc, string? azureReport)
    {
        double fraction = limit > 0 ? Math.Clamp((double)used / limit, 0, 1) : 0;
        string percent = (fraction * 100).ToString("0.#", Mexico) + " %";

        UsageMini.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UsagePercent.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        PopupUsageSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        UsageFill.Width = UsageMini.Width * UsageGauge.FillFraction(fraction); // realza lo bajo; el % es exacto
        UsagePercent.Text = percent;
        PopupUsage.Text = $"{used.ToString("N0", Mexico)} de {limit.ToString("N0", Mexico)} caracteres ({percent})";
        PopupUsageFill.Width = (StatusPanel.Width - StatusPanel.Padding.Left - StatusPanel.Padding.Right - 2) * fraction;
        // La fecha del reinicio es en UTC: pasarla a hora local la movería al día anterior en América.
        PopupReset.Text = "Se reinicia el " + resetsOnUtc.ToString("d 'de' MMMM", Mexico);
        PopupAzure.Text = azureReport ?? "Todavía no consultas el consumo real en Azure.";
        _usagePercent = visible ? percent : null;
        UpdateStatusName();
    }

    /// <summary>Línea de consumo de Google en el panel ("Google: 1 200 de 950 000 (0.1 %)"), o null si no está configurado.</summary>
    public void SetGoogleUsage(string? text)
    {
        PopupGoogleUsage.Text = text ?? "";
        PopupGoogleUsage.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Marca el motor elegido y habilita solo los que están configurados.</summary>
    public void SetEngineChoice(EngineChoice current, bool hasAzure, bool hasGoogle, bool hasPiper)
    {
        if (EngineChoicePanel.Children.Count == 0)
        {
            foreach ((EngineChoice choice, string label) in new[]
                     {
                         (EngineChoice.Auto, "Automático"), (EngineChoice.Azure, "Azure"),
                         (EngineChoice.Google, "Google"), (EngineChoice.Piper, "Piper"),
                     })
            {
                var button = new Button
                {
                    Style = (Style)Resources["BareButton"],
                    Content = label,
                    Height = 30,
                    Padding = new Thickness(10, 0, 10, 0),
                    Tag = choice,
                };
                button.SetResourceReference(FontSizeProperty, "LectorFontSize12");
                AutomationProperties.SetName(button, "Usar " + label);
                button.Click += (_, _) => EngineChoiceRequested?.Invoke(choice);
                EngineChoicePanel.Children.Add(button);
            }
        }

        foreach (Button button in EngineChoicePanel.Children.OfType<Button>())
        {
            var choice = (EngineChoice)button.Tag;
            bool on = choice == current;
            button.IsEnabled = choice switch
            {
                EngineChoice.Azure => hasAzure,
                EngineChoice.Google => hasGoogle,
                EngineChoice.Piper => hasPiper,
                _ => true,
            };
            button.ToolTip = button.IsEnabled ? null : "Configúralo en ⚙ Configuración";
            ToolTipService.SetShowOnDisabled(button, true); // si no, el aviso no aparece en un botón deshabilitado
            if (on)
            {
                button.SetResourceReference(BackgroundProperty, "LectorSurfaceBrush");
            }
            else
            {
                button.Background = Brushes.Transparent;
            }

            button.SetResourceReference(ForegroundProperty, on ? "LectorTextBrush" : "LectorText2Brush");
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            button.Opacity = button.IsEnabled ? 1 : 0.45;
        }
    }

    public void SetRefreshing(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        RefreshLabel.Text = busy ? "Consultando Azure…" : "Consultar consumo real en Azure";
    }

    private void UpdateStatusName() =>
        AutomationProperties.SetName(StatusButton,
            $"Estado: {PopupEngine.Text}" + (_usagePercent is null ? "" : $", consumo de Azure {_usagePercent}"));

    // ------------------------------------------------------------------ panel del estado (al pasar el mouse)

    private void OnStatusMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _statusCloseTimer.Stop();
        StatusPopup.IsOpen = true;
    }

    // Se cierra un momento después, para poder pasar del componente al panel sin que parpadee.
    private void OnStatusMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _statusCloseTimer.Stop();
        _statusCloseTimer.Start();
    }

    /// <summary>
    /// Como Spotify, el transporte va centrado en la ventana (las dos columnas laterales iguales). Si no alcanza el
    /// ancho, la columna derecha toma solo lo que necesita: el transporte se corre un poco, pero nada se corta.
    /// </summary>
    private void OnControlsSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    // El lado derecho también crece sin que cambie la ventana (aparece el porcentaje, cambia el motor): se recalcula.
    private void OnRightPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    private void UpdateColumns()
    {
        RightPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        TransportPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double centered = TransportPanel.DesiredSize.Width + 2 * RightPanel.DesiredSize.Width;
        GridUnitType wanted = ControlsGrid.ActualWidth >= centered ? GridUnitType.Star : GridUnitType.Auto;
        if (RightColumn.Width.GridUnitType != wanted)
        {
            RightColumn.Width = new GridLength(1, wanted);
        }
    }

    // Abre (no alterna): después de que el mouse lo abrió, un clic no debe cerrarlo. Se cierra con Esc o al salir.
    private void OnStatusClick(object sender, RoutedEventArgs e) => StatusPopup.IsOpen = true;

    // Alineado a la derecha del componente y por encima de él (el componente está en el borde derecho).
    private static CustomPopupPlacement[] PlaceStatusPanel(Size popupSize, Size targetSize, Point offset) =>
        [new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 10, -popupSize.Height + 4), PopupPrimaryAxis.Horizontal)];

    public void SetSpeed(double speed)
    {
        foreach ((Button button, double step) in _speedButtons)
        {
            bool on = Math.Abs(step - speed) < 0.001;
            if (on)
            {
                button.SetResourceReference(BackgroundProperty, "LectorSurfaceBrush");
            }
            else
            {
                button.Background = Brushes.Transparent;
            }

            button.SetResourceReference(ForegroundProperty, on ? "LectorTextBrush" : "LectorText2Brush");
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    public void SetState(PlaybackState state, bool hasDocument)
    {
        (string glyph, string label) = state switch
        {
            PlaybackState.Playing or PlaybackState.Loading => ("PauseGlyph", "Pausar"),
            PlaybackState.Paused => ("PlayGlyph", "Seguir"),
            _ => ("PlayGlyph", "Leer"),
        };
        PlayIcon.Data = (Geometry)FindResource(glyph);
        AutomationProperties.SetName(PlayButton, label);
        PlayButton.IsEnabled = hasDocument;
        PreviousButton.IsEnabled = hasDocument;
        BackButton.IsEnabled = hasDocument;
        ForwardButton.IsEnabled = hasDocument;
        NextButton.IsEnabled = hasDocument;
    }

    public static string Format(TimeSpan time) =>
        $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    private void ShowDetail(string text)
    {
        DetailText.Text = text;
        DetailText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke();
    private void OnBack10Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(TimeSpan.FromSeconds(-10));
    private void OnForward10Click(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(TimeSpan.FromSeconds(10));
    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();
    private void OnNextClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshUsageClicked?.Invoke();
}
