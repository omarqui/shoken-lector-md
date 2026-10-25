using System.ComponentModel;
using System.Windows;
using Shoken.Core.Model;
using Shoken.Core.Settings;

namespace Shoken.App.Services;

/// <summary>Una pestaña de la barra. Su estado es lo que se guarda para reabrirla al iniciar.</summary>
internal sealed class DocumentTab(OpenTab state) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public OpenTab State { get; } = state;

    /// <summary>
    /// La demo de entonación: no es un documento ni una pestaña vacía (su estado no tiene ruta) y no se guarda en la
    /// sesión.
    /// </summary>
    public bool IsDemo { get; init; }

    /// <summary>Títulos contraídos en el índice (por su ancla). Se recuerdan mientras la app está abierta.</summary>
    public HashSet<string> CollapsedOutline { get; } = new(StringComparer.Ordinal);

    /// <summary>Ya se aplicó el "contraído al primer nivel" con que abre su índice; desde ahí manda el usuario.</summary>
    public bool OutlineFoldApplied { get; set; }

    public string Name => IsDemo ? HtmlPages.DemoTitle : State.Name;

    /// <summary>Texto pegado (en memoria): su ícono va en otro color que el de los archivos .md.</summary>
    public bool IsPasted => State.IsPasted;

    /// <summary>Ícono de Segoe Fluent: bocina (la demo), documento (archivo .md), portapapeles (texto pegado) o "+" (vacía).</summary>
    public string Icon => IsDemo ? "" : State.IsEmpty ? "" : State.IsPasted ? "" : "";

    public string ToolTip => IsDemo ? "El mismo texto con y sin la entonación de Shoken"
        : State.IsEmpty ? "Pega aquí (Ctrl+V) un texto Markdown o la ruta de un archivo"
        : State.IsPasted ? "Texto pegado (en memoria): se guarda hasta que cierres la pestaña"
        : State.Path;

    /// <summary>Pestaña vacía que se puede aprovechar para abrir o pegar algo (la demo no cuenta).</summary>
    public bool IsReusable => State.IsEmpty && !IsDemo;

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
/// Un título en el panel del índice. Si está contraído, su duración, sus resultados y lo escuchado son los de toda la
/// sección.
/// </summary>
/// <param name="chapters">Los capítulos de la línea de tiempo que suma la fila.</param>
/// <param name="heardFraction">Cuánto de la sección ya se escuchó, de 0 a 1 (por duración).</param>
internal sealed class OutlineItem(OutlineEntry entry, int index, string time, int matches, bool hasChildren, bool isCollapsed,
    IReadOnlyList<TimelineChapter> chapters, double heardFraction) : INotifyPropertyChanged
{
    private double _heardFraction = Math.Clamp(heardFraction, 0, 1);

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>Con ellos se vuelve a calcular lo escuchado de la fila.</summary>
    public IReadOnlyList<TimelineChapter> Chapters { get; } = chapters;

    /// <summary>
    /// Lo escuchado de la sección: el largo de la línea fina bajo la fila (ScaleX). Al escuchar un párrafo cambia aquí,
    /// sin rehacer la lista: rehacerla cerraría los tooltips y volvería a anunciar la fila con el foco.
    /// </summary>
    public double HeardFraction
    {
        get => _heardFraction;
        set
        {
            double fraction = Math.Clamp(value, 0, 1);
            if (fraction == _heardFraction)
            {
                return;
            }

            bool wasHeard = IsHeard;
            _heardFraction = fraction;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeardFraction)));
            if (IsHeard != wasHeard)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeardCheckVisibility)));
            }
        }
    }

    private bool IsHeard => HeardFraction >= 0.999;

    /// <summary>✓ junto a la duración cuando la sección ya se escuchó completa.</summary>
    public Visibility HeardCheckVisibility => IsHeard ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Flecha de Segoe Fluent: ▸ contraído, ▾ expandido. Sin subtítulos no se ve, pero ocupa su lugar.</summary>
    public string Chevron => IsCollapsed ? "" : "";

    public Visibility ChevronVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;

    public string ChevronName => $"{(IsCollapsed ? "Expandir" : "Contraer")} «{Text}»";

    public string PlayName => $"Leer desde «{Text}»";

    public Thickness Margin => new(Entry.Indent * 14, 0, 0, 0);

    public FontWeight Weight => Entry.Indent == 0 ? FontWeights.SemiBold : FontWeights.Normal;

    // Lo que anuncia un lector de pantalla para el elemento de la lista.
    public override string ToString() =>
        Text + (IsCollapsed ? ", contraído" : "") + (IsHeard ? ", escuchada" : "") + (matches > 0 ? $", {matches} resultados" : "");
}
