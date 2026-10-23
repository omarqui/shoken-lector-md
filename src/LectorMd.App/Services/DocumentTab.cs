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

    public string Name => State.Name;

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
    }
}

/// <summary>Un título en el panel del índice.</summary>
internal sealed class OutlineItem(OutlineEntry entry)
{
    public OutlineEntry Entry { get; } = entry;

    public string Text => Entry.Text;

    public Thickness Margin => new(Entry.Indent * 14, 0, 0, 0);

    public FontWeight Weight => Entry.Indent == 0 ? FontWeights.SemiBold : FontWeights.Normal;

    // Lo que anuncia un lector de pantalla para el elemento de la lista.
    public override string ToString() => Text;
}
