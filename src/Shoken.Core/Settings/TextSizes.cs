namespace Shoken.Core.Settings;

/// <summary>Tamaño de los textos de la interfaz: Normal es el del diseño; cada paso agrega 15 %.</summary>
public enum TextSize
{
    Normal,
    Large,
    ExtraLarge,
}

public static class TextSizes
{
    public static double Scale(TextSize size) => size switch
    {
        TextSize.Large => 1.15,
        TextSize.ExtraLarge => 1.30,
        _ => 1.0,
    };

    /// <summary>El tamaño de diseño <paramref name="baseSize"/> escalado, redondeado a medio punto.</summary>
    public static double FontSize(TextSize size, double baseSize) =>
        Math.Round(baseSize * Scale(size) * 2, MidpointRounding.AwayFromZero) / 2;
}
