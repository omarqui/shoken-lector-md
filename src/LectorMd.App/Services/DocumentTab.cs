using System.ComponentModel;
using System.Windows;
using LectorMd.Core.Model;
using LectorMd.Core.Settings;

namespace LectorMd.App.Services;

/// <summary>Una pestaña de la barra. Su estado es lo que se guarda para reabrirla al iniciar.</summary>
internal sealed class DocumentTab(OpenTab state) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public OpenTab State { get; } = state;

    /// <summary>Títulos contraídos en el índice (por su ancla). Se recuerdan mientras la app está abierta.</summary>
    public HashSet<string> CollapsedOutline { get; } = new(StringComparer.Ordinal);

    public string Name => State.Name;

    /// <summary>Texto pegado (en memoria): su ícono va en otro color que el de los archivos .md.</summary>
    public bool IsPasted => State.IsPasted;

    /// <summary>Ícono de Segoe Fluent: documento (archivo .md), portapapeles (texto pegado) o "+" (vacía).</summary>
    public string Icon => State.IsEmpty ? "" : State.IsPasted ? "" : "";

    public string ToolTip => State.IsEmpty ? "Pega aquí (Ctrl+V) un texto Markdown o la ruta de un archivo"
        : State.IsPasted ? "Texto pegado (en memoria): se guarda hasta que cierres la pestaña"
        : State.Path;

    public bool IsFile(string path) =>
        !State.IsEmpty && !State.IsPasted && string.Equals(State.Path, path, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name;

    /// <summary>Avisa a la barra que cambió el nombre (al pegar en una pestaña vacía).</summary>
    public void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPasted)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
    }
}

/// <summary>
/// Un título en el panel del índice. Si está contraído, su duración y sus resultados son los de toda la sección.
/// </summary>
internal sealed class OutlineItem(OutlineEntry entry, int index, string time, int matches, bool hasChildren, bool isCollapsed)
{
    public OutlineEntry Entry { get; } = entry;

    /// <summary>Posición en el índice completo (con las secciones contraídas, no coincide con la fila).</summary>
    public int Index { get; } = index;

    public bool HasChildren { get; } = hasChildren;

    public bool IsCollapsed { get; } = isCollapsed;

    public string Text => Entry.Text;

    /// <summary>Duración de la sección ("01:12", "≈00:58"), o vacío si no se sabe.</summary>
    public string Time { get; } = time;

    /// <summary>Resultados de la búsqueda (Ctrl+F) en la sección; la insignia solo se ve si hay alguno.</summary>
    public string Matches => matches.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public Visibility MatchesVisibility => matches > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Flecha de Segoe Fluent: ▸ contraído, ▾ expandido. Sin subtítulos no se ve, pero ocupa su lugar.</summary>
    public string Chevron => IsCollapsed ? "" : "";

    public Visibility ChevronVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;

    public string ChevronName => $"{(IsCollapsed ? "Expandir" : "Contraer")} «{Text}»";

    public string PlayName => $"Leer desde «{Text}»";

    public Thickness Margin => new(Entry.Indent * 14, 0, 0, 0);

    public FontWeight Weight => Entry.Indent == 0 ? FontWeights.SemiBold : FontWeights.Normal;

    // Lo que anuncia un lector de pantalla para el elemento de la lista.
    public override string ToString() =>
        Text + (IsCollapsed ? ", contraído" : "") + (matches > 0 ? $", {matches} resultados" : "");
}
