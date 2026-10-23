using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core.Model;
using LectorMd.Core.Settings;

namespace LectorMd.App.Controls;

/// <summary>
/// Reproductor de abajo: línea de tiempo por capítulos, transporte, sección y párrafo, voz, velocidad y consumo.
/// Solo muestra lo que le dicen y avisa lo que se pulsa; la lógica vive en la ventana.
/// </summary>
public partial class PlayerBar : UserControl
{
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _noticeTimer;
    private readonly List<(Border Fill, TextBlock Label, TimelineChapter Chapter)> _cells = [];
    private readonly List<(Button Button, double Speed)> _speedButtons = [];
    private ReadingTimeline? _timeline;
    private string _detail = "";
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

        foreach (double speed in PlaybackSpeeds.Steps)
        {
            var button = new Button
            {
                Style = (Style)Resources["BareButton"],
                Content = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×",
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 13,
            };
            AutomationProperties.SetName(button, "Velocidad " + button.Content);
            button.Click += (_, _) => SpeedChanged?.Invoke(speed);
            SpeedPanel.Children.Add(button);
            _speedButtons.Add((button, speed));
        }

        SetTimeline(null);
    }

    public event Action? PlayPauseClicked;
    public event Action? StopClicked;
    public event Action? PreviousClicked;
    public event Action? NextClicked;
    public event Action<double>? SpeedChanged;
    public event Action? RefreshUsageClicked;

    /// <summary>Clic en un tramo de la línea de tiempo: el ancla del título (null = desde el principio).</summary>
    public event Action<string?>? ChapterClicked;

    /// <summary>Arma la línea de tiempo; null = no hay documento (pestaña vacía o bienvenida).</summary>
    public void SetTimeline(ReadingTimeline? timeline)
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

            var label = new TextBlock { Text = chapter.Title, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "LectorText2Brush");

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

        SetProgress(TimeSpan.Zero, -1);
    }

    /// <summary>Rellena lo escuchado hasta <paramref name="elapsed"/> y marca el capítulo actual.</summary>
    public void SetProgress(TimeSpan elapsed, int chapterIndex)
    {
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

    /// <summary>Un aviso (antes iba en la barra de estado): se queda 8 s sobre el detalle de la posición.</summary>
    public void ShowNotice(string text)
    {
        _noticeShowing = true;
        ShowDetail(text);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void SetEngine(string name, string detail, Brush dot, string? tooltip)
    {
        EngineName.Text = name;
        EngineDetail.Text = detail;
        EngineDot.Fill = dot;
        EngineChip.ToolTip = tooltip;
    }

    public void SetUsage(bool visible, string text, double fraction, string tooltip, string? lastQuery)
    {
        UsageBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UsageText.Text = text;
        UsageFill.Width = 150 * Math.Clamp(fraction, 0, 1);
        UsageBox.ToolTip = tooltip;
        RefreshTime.Text = lastQuery ?? "";
        RefreshTime.Visibility = lastQuery is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetRefreshing(bool busy) => RefreshButton.IsEnabled = !busy;

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
        (PlayIcon.Text, string label) = state switch
        {
            PlaybackState.Playing or PlaybackState.Loading => ("", "Pausar"),
            PlaybackState.Paused => ("", "Seguir"),
            _ => ("", "Leer"),
        };
        AutomationProperties.SetName(PlayButton, label);
        PlayButton.IsEnabled = hasDocument;
        PreviousButton.IsEnabled = hasDocument;
        NextButton.IsEnabled = hasDocument;
        StopButton.IsEnabled = state != PlaybackState.Stopped;
    }

    public static string Format(TimeSpan time) =>
        $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";

    private void ShowDetail(string text)
    {
        DetailText.Text = text;
        DetailText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke();
    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();
    private void OnNextClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshUsageClicked?.Invoke();
}
