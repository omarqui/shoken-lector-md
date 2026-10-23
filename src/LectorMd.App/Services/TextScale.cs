using System.Windows;
using LectorMd.Core.Settings;

namespace LectorMd.App.Services;

/// <summary>
/// Tamaño de los textos de la interfaz. La XAML usa <c>{DynamicResource LectorFontSize12}</c> (el número es el tamaño
/// de diseño) y aquí se le da el valor escalado. Cada ventana toma <c>LectorFontSize14</c> como su FontSize y los
/// controles del tema Fluent lo heredan (sobrescribir sus claves, como ControlContentThemeFontSize, no los cambia).
/// Los íconos no se escalan.
/// </summary>
internal static class TextScale
{
    private static readonly double[] DesignSizes = [10, 11, 12, 13, 14, 15, 16, 17, 18];

    public static void Apply(TextSize size)
    {
        ResourceDictionary resources = Application.Current.Resources;
        foreach (double designSize in DesignSizes)
        {
            resources[$"LectorFontSize{designSize}"] = TextSizes.FontSize(size, designSize);
        }

        // El índice crece con su texto: así le caben los mismos títulos que en Normal.
        resources["LectorOutlineWidth"] = Math.Round(250 * TextSizes.Scale(size));
    }
}
