#:package Svg.Skia@5.2.3

// Genera src/Shoken.App/Assets/app.ico a partir de logo.svg, con un PNG por tamaño: Windows elige el que necesita
// (16 o 20 en la bandeja, 32 en la barra de tareas, 256 en el Explorador) y empaquetar.ps1 saca del de 256 los logos
// de la Store. Uso, desde la raíz del repo: dotnet run empaquetado/generar-icono.cs

using System.Runtime.CompilerServices;
using SkiaSharp;
using Svg.Skia;

string assets = Path.GetFullPath(Path.Combine(Here(), "..", "src", "Shoken.App", "Assets"));
string svgPath = Path.Combine(assets, "logo.svg");
string icoPath = Path.Combine(assets, "app.ico");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

using var svg = new SKSvg();
SKPicture picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"No se pudo leer {svgPath}.");
float side = picture.CullRect.Width;

var pngs = new List<byte[]>();
foreach (int size in sizes)
{
    using SKSurface surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.Scale(size / side);
    surface.Canvas.DrawPicture(picture);
    using SKImage image = surface.Snapshot();
    using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
    pngs.Add(png.ToArray());
}

// Formato ICO: cabecera, una entrada de 16 bytes por tamaño y después los PNG tal cual (Windows los acepta desde Vista).
using (var ico = new BinaryWriter(File.Create(icoPath)))
{
    ico.Write((short)0);
    ico.Write((short)1); // tipo: ícono
    ico.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] % 256)); // 256 se escribe como 0
        ico.Write((byte)(sizes[i] % 256));
        ico.Write((byte)0); // sin paleta
        ico.Write((byte)0);
        ico.Write((short)1); // planos
        ico.Write((short)32); // bits por píxel
        ico.Write(pngs[i].Length);
        ico.Write(offset);
        offset += pngs[i].Length;
    }

    foreach (byte[] png in pngs)
    {
        ico.Write(png);
    }
}

Console.WriteLine($"Listo: {icoPath} ({string.Join(", ", sizes)} px)");

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
