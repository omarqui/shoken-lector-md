namespace LectorMd.Core.Usage;

/// <summary>Relleno de la barra compacta de consumo.</summary>
public static class UsageGauge
{
    /// <summary>
    /// Fracción de la barra a rellenar. Usa raíz cuadrada para que los consumos bajos (los de casi todo el mes)
    /// se vean: 4 % llena un quinto, 25 % la mitad y 100 % la barra completa. El porcentaje exacto va al lado.
    /// </summary>
    public static double FillFraction(double fraction) => Math.Sqrt(Math.Clamp(fraction, 0, 1));
}
