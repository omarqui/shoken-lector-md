using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using LectorMd.App.Controls;
using LectorMd.App.Services;
using LectorMd.Core.Settings;

namespace LectorMd.App;

/// <summary>
/// Reproductor flotante para cuando la ventana principal está oculta o minimizada (por ejemplo, al leer con
/// Ctrl+Alt+L). No roba el foco y se cierra solo tras unos segundos en pausa, detenido o al terminar.
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    private static readonly TimeSpan AutoCloseAfter = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _autoClose;
    private PlaybackState _state = PlaybackState.Stopped;
    private double _speed = 1.0;
    private string _detail = "";
    private bool _moved;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        _autoClose = new DispatcherTimer { Interval = AutoCloseAfter };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose.Stop();
            Close();
        };
        // Abajo a la derecha, sobre la barra de tareas (el margen de la tarjeta separa del borde). El alto sale del
        // contenido, así que se acomoda cada vez que cambia (y el botón de play nunca queda cortado).
        SizeChanged += (_, _) =>
        {
            if (!_moved)
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth;
                Top = area.Bottom - ActualHeight;
            }
        };
        Closed += (_, _) => _autoClose.Stop();
    }

    public event Action? PlayPauseClicked;
    public event Action? StopClicked;
    public event Action? PreviousClicked;
    public event Action? NextClicked;
    public event Action<double>? SpeedChanged;
    public event Action? OpenMainClicked;

    public void SetPosition(string section, string detail)
    {
        SectionText.Text = section;
        _detail = detail;
        DetailText.Text = _autoClose.IsEnabled ? DetailText.Text : detail;
    }

    public void ShowNotice(string text) => DetailText.Text = text;

    public void SetProgress(TimeSpan elapsed, TimeSpan total)
    {
        ElapsedText.Text = PlayerBar.Format(elapsed);
        TimeSpan remaining = total - elapsed;
        RemainingText.Text = "−" + PlayerBar.Format(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
        ProgressScale.ScaleX = total > TimeSpan.Zero ? Math.Clamp(elapsed / total, 0, 1) : 0;
    }

    public void SetSpeed(double speed)
    {
        _speed = speed;
        SpeedText.Text = speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
        AutomationProperties.SetName(SpeedButton, $"Velocidad {SpeedText.Text}");
    }

    public void SetState(PlaybackState state)
    {
        _state = state;
        bool playing = state is PlaybackState.Playing or PlaybackState.Loading;
        PlayIcon.Text = playing ? "" : "";
        AutomationProperties.SetName(PlayButton, playing ? "Pausar" : state == PlaybackState.Paused ? "Seguir" : "Leer");
        StopButton.IsEnabled = state != PlaybackState.Stopped;
        UpdateAutoClose();
    }

    /// <summary>En pausa o detenido cuenta para cerrarse; leyendo o con el mouse encima, no.</summary>
    private void UpdateAutoClose()
    {
        bool reading = _state is PlaybackState.Playing or PlaybackState.Loading;
        if (reading)
        {
            _autoClose.Stop();
            DetailText.Text = _detail;
        }
        else if (IsMouseOver)
        {
            _autoClose.Stop();
        }
        else if (!_autoClose.IsEnabled)
        {
            _autoClose.Start();
            DetailText.Text = (_state == PlaybackState.Paused ? "En pausa" : "Detenido")
                              + $" · se cierra solo en {AutoCloseAfter.TotalSeconds:0} s";
        }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => _autoClose.Stop();

    private void OnMouseLeave(object sender, MouseEventArgs e) => UpdateAutoClose();

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            _moved = true; // el usuario lo puso donde quiere: ya no se reacomoda solo
        }
    }

    // El siguiente paso de velocidad; después de 1.75× vuelve a 0.75×.
    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<double> steps = PlaybackSpeeds.Steps;
        int index = steps.ToList().FindIndex(s => Math.Abs(s - _speed) < 0.001);
        SpeedChanged?.Invoke(steps[(index + 1) % steps.Count]);
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => PlayPauseClicked?.Invoke();
    private void OnStopClick(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnPreviousClick(object sender, RoutedEventArgs e) => PreviousClicked?.Invoke();
    private void OnNextClick(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    private void OnOpenMainClick(object sender, RoutedEventArgs e) => OpenMainClicked?.Invoke();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
