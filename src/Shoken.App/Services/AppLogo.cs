using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Shoken.App.Services;

/// <summary>
/// El logo de la app para la interfaz, sacado de Assets/app.ico (que generar-icono.cs dibuja desde logo.svg en varios
/// tamaños): así no hay una segunda copia del dibujo que mantener.
/// </summary>
internal static class AppLogo
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/app.ico");

    /// <summary>
    /// Pone en <paramref name="image"/> el cuadro del ícono que mejor encaja en su tamaño con la escala de la pantalla:
    /// cada tamaño del .ico se dibujó para ese tamaño, y escalar el de 256 a 18 px borronea los trazos finos.
    /// </summary>
    public static void Apply(System.Windows.Controls.Image image)
    {
        void Update() => image.Source = Frame(image.Width * VisualTreeHelper.GetDpi(image).DpiScaleX);
        image.Loaded += (_, _) => Update();
        image.DpiChanged += (_, _) => Update();
    }

    private static BitmapFrame Frame(double pixels)
    {
        var decoder = BitmapDecoder.Create(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.Where(f => f.PixelWidth >= pixels).MinBy(f => f.PixelWidth)
            ?? decoder.Frames.MaxBy(f => f.PixelWidth)!;
    }
}
