namespace LectorMd.Core.Settings;

/// <summary>Velocidades del selector de reproducción.</summary>
public static class PlaybackSpeeds
{
    public static IReadOnlyList<double> Steps { get; } = [0.75, 1.0, 1.25, 1.5, 1.75];

    /// <summary>El paso más cercano (para velocidades guardadas con el deslizador anterior).</summary>
    public static double Nearest(double value) => Steps.MinBy(step => Math.Abs(step - value));
}
