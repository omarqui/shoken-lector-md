using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Shoken.App.Services;

/// <summary>
/// Ícono de Shoken en la bandeja del sistema: la app sigue ahí al cerrar la ventana.
/// El menú es de WPF para que tome el tema Fluent (el de WinForms se ve antiguo y no sigue el modo oscuro).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly ContextMenu _menu;
    private readonly Drawing.Icon _image;

    public TrayIcon(Action open, Action readClipboard, Action exit)
    {
        _menu = new ContextMenu();
        _menu.Items.Add(Item("Abrir Shoken", open, bold: true));
        _menu.Items.Add(Item("Leer portapapeles", readClipboard, gesture: "Ctrl+Alt+L"));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Salir", exit));

        _image = LoadIcon();
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Shoken · Ctrl+Alt+L lee lo que copiaste", Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                ShowMenu();
            }
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                open();
            }
        };
    }

    public void ShowHint(string text) => _icon.ShowBalloonTip(6000, "Shoken", text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        // Sin esto queda un "ícono fantasma" en la bandeja hasta que se pasa el mouse por encima.
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    private void ShowMenu()
    {
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        // Para que se cierre al hacer clic afuera, el menú tiene que estar en primer plano.
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
        {
            SetForegroundWindow(source.Handle);
        }
    }

    private static MenuItem Item(string header, Action action, bool bold = false, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (bold)
        {
            item.FontWeight = FontWeights.SemiBold;
        }

        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>El ícono de la app (Assets/app.ico) en el tamaño de la bandeja con la escala actual (16, 20, 24 o 32 px).</summary>
    private static Drawing.Icon LoadIcon()
    {
        using Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
