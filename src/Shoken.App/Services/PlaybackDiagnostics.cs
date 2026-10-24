using System.Diagnostics;
using System.Globalization;
using Shoken.Core.Settings;

namespace Shoken.App.Services;

/// <summary>
/// Mediciones del arranque de la lectura (tiempos y posición real del reproductor), para diagnosticar silencios
/// o inicios cortados. Solo se activa con la variable de entorno LECTORMD_DIAGNOSTICO=1 y escribe en
/// %LOCALAPPDATA%\Shoken\diagnostico.log.
/// </summary>
internal static class PlaybackDiagnostics
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("LECTORMD_DIAGNOSTICO") == "1";

    private static readonly Stopwatch Clock = new();

    /// <summary>Empieza a medir desde cero (al pedir leer desde un párrafo).</summary>
    public static void Restart(string what)
    {
        if (!Enabled)
        {
            return;
        }

        Clock.Restart();
        Write($"---- {what}");
    }

    public static void Log(string what)
    {
        if (Enabled)
        {
            Write($"{Clock.ElapsedMilliseconds,6} ms  {what}");
        }
    }

    private static void Write(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Default.Root, "diagnostico.log"),
                DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
