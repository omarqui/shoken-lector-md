namespace Shoken.Core.Model;

/// <summary>Qué hacer cuando el archivo abierto cambió en el disco.</summary>
public enum ReloadAction
{
    /// <summary>El texto es el mismo: no hacer nada.</summary>
    Ignore,

    /// <summary>Recargar ya.</summary>
    ReloadNow,

    /// <summary>Preguntar al usuario antes de recargar.</summary>
    Ask,

    /// <summary>Esperar a que la lectura se pause.</summary>
    WhenPaused,

    /// <summary>Esperar a que el usuario termine de escribir su apunte.</summary>
    WhenEditingEnds,
}

public static class ReloadPolicy
{
    public static ReloadAction Decide(bool contentChanged, bool playing, bool windowInFront, bool editingNote)
    {
        if (!contentChanged)
        {
            return ReloadAction.Ignore;
        }

        if (editingNote)
        {
            return ReloadAction.WhenEditingEnds;
        }

        if (!playing)
        {
            return ReloadAction.ReloadNow;
        }

        return windowInFront ? ReloadAction.Ask : ReloadAction.WhenPaused;
    }
}
