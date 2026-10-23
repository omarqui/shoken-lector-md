using System.Globalization;

namespace LectorMd.Core.Settings;

/// <summary>Velocidades del selector de reproducción.</summary>
public static class PlaybackSpeeds
{
    public static IReadOnlyList<double> Steps { get; } = [0.75, 1.0, 1.25, 1.5, 1.75];

    /// <summary>El paso más cercano (para velocidades guardadas con el deslizador anterior).</summary>
    public static double Nearest(double value) => Steps.MinBy(step => Math.Abs(step - value));

    /// <summary>El paso anterior al de <paramref name="speed"/>, o null si ya es el más lento.</summary>
    public static double? Slower(double speed) => StepAt(IndexOf(speed) - 1);

    /// <summary>El paso siguiente al de <paramref name="speed"/>, o null si ya es el más rápido.</summary>
    public static double? Faster(double speed) => StepAt(IndexOf(speed) + 1);

    /// <summary>"1×", "1.25×": sin ceros de más.</summary>
    public static string Label(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    private static int IndexOf(double speed) => Steps.ToList().IndexOf(Nearest(speed));

    private static double? StepAt(int index) => index >= 0 && index < Steps.Count ? Steps[index] : null;
}
