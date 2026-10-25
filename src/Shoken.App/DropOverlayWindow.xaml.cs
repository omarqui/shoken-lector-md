using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Shoken.Core.Desktop;

namespace Shoken.App;

/// <summary>
/// Capa que tapa la ventana principal mientras se arrastran archivos sobre ella: dice qué se abrirá al soltar (o por
/// qué no se puede) y es la que recibe los archivos. Es una ventana aparte porque el documento (WebView2) tapa todo lo
/// que WPF dibuje encima, y porque la página no ve los nombres de los archivos antes de que se suelten.
/// </summary>
public sealed partial class DropOverlayWindow : Window
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly DispatcherTimer _watch;
    private DraggedFiles? _files;

    public DropOverlayWindow()
    {
        InitializeComponent();
        // Red de seguridad: si el arrastre termina sin pasar por la capa (se soltó o se canceló antes de mover el mouse
        // sobre ella), no se queda tapando la ventana.
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _watch.Tick += (_, _) =>
        {
            if (!IsPressed(VkLButton) && !IsPressed(VkRButton))
            {
                Hide();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _watch.Stop();
            }
        };
    }

    /// <summary>Se soltaron archivos: solo los que se pueden abrir, en el orden en que vienen.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    /// <summary>
    /// Tapa la ventana dueña, con el tinte y el marco alineados a <paramref name="content"/>. <paramref name="data"/> es
    /// lo arrastrado, o null si todavía no se sabe (el arrastre entró por el documento): entonces dice un "Suelta para
    /// abrir" genérico hasta que el mouse se mueve sobre la capa. Si ya está a la vista, solo se actualiza.
    /// </summary>
    public void ShowOver(FrameworkElement content, IDataObject? data)
    {
        if (data is not null || !IsVisible)
        {
            _files = data is null ? null : Describe(data);
            ShowPreview(_files);
        }

        if (IsVisible)
        {
            return;
        }

        // Toda la ventana dueña, en píxeles: así no hay un borde por donde el arrastre vuelva a ella sin pasar por la capa.
        GetWindowRect(new WindowInteropHelper(Owner).Handle, out NativeRect bounds);
        SetWindowPos(new WindowInteropHelper(this).EnsureHandle(), IntPtr.Zero, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SwpNoZOrder | SwpNoActivate);
        Point offset = content.TranslatePoint(new Point(), Owner);
        Root.Margin = new Thickness(offset.X, offset.Y,
            Math.Max(0, Owner.ActualWidth - offset.X - content.ActualWidth), Math.Max(0, Owner.ActualHeight - offset.Y - content.ActualHeight));

        Show();
        Root.BeginAnimation(OpacityProperty, SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            : null);
        _watch.Start();
    }

    private void ShowPreview(DraggedFiles? files)
    {
        bool canDrop = files?.CanDrop ?? true;
        string color = canDrop ? "LectorAccentBrush" : "LectorText2Brush";
        Tint.SetResourceReference(Shape.FillProperty, color);
        Frame.SetResourceReference(Shape.StrokeProperty, color);
        Badge.SetResourceReference(Border.BackgroundProperty, color);
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, canDrop ? "LectorOnAccentBrush" : "LectorSurfaceBrush");
        Glyph.Text = canDrop ? "" : ""; // descargar / prohibido
        string title = files?.Title ?? "Suelta para abrir";
        DetailText.Text = files?.Detail ?? "";
        DetailText.Visibility = files?.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        if (TitleText.Text != title)
        {
            TitleText.Text = title;
            UIElementAutomationPeer.CreatePeerForElement(TitleText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _files = Describe(e.Data);
        ShowPreview(_files);
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _files is { CanDrop: true } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Hide();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Hide();
        if (Describe(e.Data) is { CanDrop: true } files)
        {
            FilesDropped?.Invoke(files.Openable);
        }
    }

    /// <summary>Sin rutas (un adjunto de correo, algo de dentro de un .zip) también se describe: no se puede abrir.</summary>
    private static DraggedFiles Describe(IDataObject data)
    {
        string[] paths;
        try
        {
            paths = data.GetData(DataFormats.FileDrop) as string[] ?? [];
        }
        catch (COMException)
        {
            paths = []; // el origen del arrastre no supo entregar la lista
        }

        return DraggedFiles.Describe(paths, File.Exists);
    }

    private static bool IsPressed(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
