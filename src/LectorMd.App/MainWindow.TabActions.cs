using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core.Desktop;
using LectorMd.Core.Settings;
using Microsoft.Win32;

namespace LectorMd.App;

/// <summary>
/// De dónde viene el documento: la ruta bajo el título (al pasar el mouse; un clic la copia), el menú del clic
/// derecho sobre una pestaña y el aviso breve que confirma lo que se hizo.
/// </summary>
public partial class MainWindow
{
    private string? _headerFile; // archivo de la pestaña activa; null si es texto pegado o no hay documento
    private DispatcherTimer? _toastTimer;

    // ------------------------------------------------------------------ título y ruta

    private void SetDocumentHeader(string title, DocumentTab? tab)
    {
        DocumentTitle.Text = title;
        _headerFile = tab is { State.IsEmpty: false, State.IsPasted: false } ? tab.State.Path : null;
        DocumentPath.Text = tab is null or { State.IsEmpty: true } ? "" : _headerFile ?? "Texto pegado (en memoria)";
        DocumentPath.Cursor = _headerFile is null ? null : Cursors.Hand;
        DocumentPath.ToolTip = _headerFile is null ? null : $"{_headerFile}\nClic para copiar la ruta";
        ShowHeaderPath(DocumentHeader.IsMouseOver);
    }

    private void ShowHeaderPath(bool show) =>
        DocumentPath.Visibility = show && DocumentPath.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnDocumentHeaderMouseEnter(object sender, MouseEventArgs e) => ShowHeaderPath(true);

    private void OnDocumentHeaderMouseLeave(object sender, MouseEventArgs e) => ShowHeaderPath(false);

    private void OnDocumentPathClick(object sender, MouseButtonEventArgs e)
    {
        if (_headerFile is { } path)
        {
            CopyToClipboard(path, "Ruta copiada");
        }
    }

    // ------------------------------------------------------------------ mover pestañas

    private const string TabDragFormat = "LectorMd.Pestaña";
    private Point? _tabDragStart;
    private DocumentTab? _draggedTab;

    private void OnTabPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _tabDragStart = IsInsideButton(e.OriginalSource as DependencyObject) ? null : e.GetPosition(TabList); // la × no arrastra

    private void OnTabPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed
            || sender is not ListBoxItem { DataContext: DocumentTab tab } item)
        {
            return;
        }

        Vector moved = e.GetPosition(TabList) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _tabDragStart = null;
        _draggedTab = tab;
        try
        {
            DragDrop.DoDragDrop(item, new DataObject(TabDragFormat, tab), DragDropEffects.Move);
        }
        finally
        {
            _draggedTab = null;
            SaveSession(); // el orden nuevo se recuerda al reabrir
        }
    }

    /// <summary>
    /// La pestaña arrastrada toma el lugar de la que está debajo apenas pasa su mitad (con anchos distintos, si se
    /// moviera antes, saltaría de ida y vuelta).
    /// </summary>
    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        if (_draggedTab is not { } dragged || !e.Data.GetDataPresent(TabDragFormat))
        {
            return; // un archivo arrastrado sobre las pestañas lo atiende la ventana
        }

        e.Handled = true;
        e.Effects = DragDropEffects.Move;
        if (sender is not ListBoxItem { DataContext: DocumentTab target } item)
        {
            return;
        }

        int from = _tabs.IndexOf(dragged);
        int to = _tabs.IndexOf(target);
        double x = e.GetPosition(item).X;
        if (from < 0 || to < 0 || from == to || (to > from && x < item.ActualWidth / 2) || (to < from && x > item.ActualWidth / 2))
        {
            return;
        }

        _syncingTabs = true; // mover no es elegir otra pestaña
        _tabs.Move(from, to);
        _syncingTabs = false;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(TabDragFormat))
        {
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ menú de la pestaña

    private void OnTabContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: DocumentTab tab, ContextMenu: { } menu })
        {
            FillTabMenu(menu, tab);
        }
    }

    private void FillTabMenu(ContextMenu menu, DocumentTab tab)
    {
        menu.Items.Clear();
        OpenTab state = tab.State;
        if (!state.IsEmpty && !state.IsPasted)
        {
            string path = state.Path;
            menu.Items.Add(MenuItem("Abrir en el editor", "", () => OpenInEditor(path)));
            menu.Items.Add(MenuItem("Abrir carpeta contenedora", "", () => OpenContainingFolder(path)));
            menu.Items.Add(MenuItem("Recargar", "", () => _ = ReloadTabAsync(tab), gesture: tab == _active ? "F5" : null));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Copiar ruta", "", () => CopyToClipboard(path, "Ruta copiada")));
            menu.Items.Add(MenuItem("Copiar nombre del archivo", "", () => CopyToClipboard(Path.GetFileName(path), "Nombre copiado")));
        }

        if (!state.IsEmpty)
        {
            if (state.IsPasted)
            {
                menu.Items.Add(MenuItem("Guardar como .md…", "", () => _ = SavePastedAsync(tab)));
            }

            menu.Items.Add(MenuItem("Copiar el texto Markdown", state.IsPasted ? "" : "", () => CopyMarkdown(state.Path)));
            menu.Items.Add(new Separator());
        }

        List<DocumentTab> others = _tabs.Where(t => t != tab).ToList();
        List<DocumentTab> toTheRight = _tabs.Skip(_tabs.IndexOf(tab) + 1).ToList();
        menu.Items.Add(MenuItem("Cerrar", "", () => _ = CloseTabAsync(tab), gesture: tab == _active ? "Ctrl+W" : null));
        menu.Items.Add(MenuItem("Cerrar las demás", "", () => _ = CloseTabsAsync(others), enabled: others.Count > 0));
        menu.Items.Add(MenuItem("Cerrar las de la derecha", "", () => _ = CloseTabsAsync(toTheRight), enabled: toTheRight.Count > 0));
        menu.Items.Add(MenuItem("Cerrar todas", "", () => _ = CloseTabsAsync(_tabs.ToList())));
    }

    private static MenuItem MenuItem(string header, string glyph, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            Icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.Resources["IconText"], FontSize = 14 },
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Cierra varias pestañas: primero las que no se ven, para no mostrar cada una al cerrar la activa. Si entre
    /// ellas hay textos pegados se pregunta, porque cerrarlos los borra.
    /// </summary>
    private async Task CloseTabsAsync(IReadOnlyList<DocumentTab> tabs)
    {
        int pasted = tabs.Count(t => t.State.IsPasted);
        if (pasted > 0 && MessageBox.Show(this,
                pasted == 1 ? "Se cerrará un texto pegado y se perderá. ¿Continuar?" : $"Se cerrarán {pasted} textos pegados y se perderán. ¿Continuar?",
                "Cerrar pestañas", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (DocumentTab tab in tabs.OrderBy(t => t == _active))
        {
            await CloseTabAsync(tab);
        }
    }

    private async Task ReloadTabAsync(DocumentTab tab)
    {
        if (tab != _active)
        {
            await ActivateAsync(tab);
        }

        await ReloadAsync();
    }

    /// <summary>Abre el archivo para corregirlo: en VS Code si está instalado; si no, en el Bloc de notas.</summary>
    private void OpenInEditor(string path)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? vsCode = EditorLocator.FindVsCode(Environment.GetEnvironmentVariable("PATH"),
            [
                Path.Combine(programs, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            ],
            File.Exists);
        try
        {
            var start = new ProcessStartInfo(vsCode ?? "notepad.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start);
            ShowToast(vsCode is null ? "Abierto en el Bloc de notas" : "Abierto en VS Code · F5 recarga al volver");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo abrir el editor.", ok: false);
        }
    }

    private void CopyMarkdown(string path)
    {
        try
        {
            CopyToClipboard(File.ReadAllText(path), "Markdown copiado");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo leer el archivo.", ok: false);
        }
    }

    /// <summary>
    /// Guarda un texto pegado en un .md elegido por el usuario: la pestaña pasa a ser de ese archivo (con su ruta y
    /// su carpeta) y la copia interna de "pegados" se borra.
    /// </summary>
    private async Task SavePastedAsync(DocumentTab tab)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Guardar el texto pegado",
            FileName = OpenTab.SuggestFileName(tab.State.Title ?? ""),
            DefaultExt = ".md",
            AddExtension = true,
            Filter = "Markdown (*.md)|*.md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string pastedCopy = tab.State.Path;
        try
        {
            File.Copy(pastedCopy, dialog.FileName, overwrite: true); // el diálogo ya preguntó si reemplazar
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo guardar el archivo.", ok: false);
            return;
        }

        MoveNotes(pastedCopy, dialog.FileName);
        tab.State.Path = dialog.FileName;
        tab.State.IsPasted = false;
        tab.State.Title = null;
        tab.NotifyChanged();
        TryDelete(pastedCopy);
        if (tab == _active)
        {
            int resume = _player.CurrentSegmentId;
            await ActivateAsync(tab, force: true, resumeSegmentId: resume >= 0 ? resume : null); // ruta, carpeta e imágenes
        }

        SaveSession();
        ShowToast($"Guardado como {Path.GetFileName(dialog.FileName)}");
    }

    /// <summary>El Explorador con el archivo seleccionado (o su carpeta, si el archivo ya no está).</summary>
    private void OpenContainingFolder(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            else
            {
                ShowToast("La carpeta del archivo ya no existe.", ok: false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo abrir el Explorador.", ok: false);
        }
    }

    // ------------------------------------------------------------------ portapapeles y aviso

    private void CopyToClipboard(string text, string done)
    {
        try
        {
            // Por RDP el portapapeles suele estar tomado un instante; WPF reintenta solo antes de fallar.
            Clipboard.SetDataObject(text, true);
            ShowToast(done);
        }
        catch (ExternalException ex)
        {
            ErrorLog.Write(ex);
            ShowToast("No se pudo copiar: otra app está usando el portapapeles.", ok: false);
        }
    }

    /// <summary>Aviso breve centrado bajo la barra de comandos; se desvanece solo.</summary>
    private void ShowToast(string message, bool ok = true)
    {
        ToastText.Text = message;
        ToastIcon.Text = ok ? "" : ""; // marca o advertencia
        ToastPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height - 4), PopupPrimaryAxis.Horizontal)];
        ToastPopup.IsOpen = false; // al reabrirse se vuelve a centrar con el ancho del texto nuevo
        ToastPopup.IsOpen = true;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        UIElementAutomationPeer.CreatePeerForElement(ToastText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (!timer.IsEnabled) // si llegó otro aviso mientras se desvanecía, se queda
                {
                    ToastPopup.IsOpen = false;
                }
            };
            Toast.BeginAnimation(OpacityProperty, fade);
        };
        return timer;
    }
}
