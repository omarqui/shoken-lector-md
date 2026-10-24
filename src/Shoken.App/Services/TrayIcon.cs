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
    private readonly IntPtr _iconHandle;

    public TrayIcon(Action open, Action readClipboard, Action exit)
    {
        _menu = new ContextMenu();
        _menu.Items.Add(Item("Abrir Shoken", open, bold: true));
        _menu.Items.Add(Item("Leer portapapeles", readClipboard, gesture: "Ctrl+Alt+L"));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item("Salir", exit));

        (Drawing.Icon icon, _iconHandle) = CreateIcon();
        _icon = new Forms.NotifyIcon { Icon = icon, Text = "Shoken · Ctrl+Alt+L lee lo que copiaste", Visible = true };
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
        DestroyIcon(_iconHandle);
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

    /// <summary>El ícono de la app: cuadrado redondeado con el acento y el altavoz de Segoe Fluent Icons.</summary>
    private static (Drawing.Icon Icon, IntPtr Handle) CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var accent = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x00, 0x5F, 0xB8));
            g.FillPath(accent, path);

            using var font = new Drawing.Font("Segoe Fluent Icons", 15, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), format);
        }

        IntPtr handle = bitmap.GetHicon();
        return (Drawing.Icon.FromHandle(handle), handle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
