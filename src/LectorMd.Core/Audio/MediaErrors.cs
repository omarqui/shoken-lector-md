using System.Runtime.InteropServices;

namespace LectorMd.Core.Audio;

/// <summary>Errores del reproductor de Windows que no son fallas de la app.</summary>
public static class MediaErrors
{
    /// <summary>NS_E_WMPCORE_AUDIO_DEVICE_LOST: la salida de audio se desconectó o cambió.</summary>
    private const int AudioDeviceLost = unchecked((int)0xC00D11E5);

    /// <summary>
    /// True si la salida de audio desapareció en plena reproducción: al desconectarse Escritorio remoto,
    /// al quitar unos audífonos o al cambiar la salida predeterminada.
    /// </summary>
    public static bool IsAudioDeviceLost(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is COMException { HResult: AudioDeviceLost })
            {
                return true;
            }
        }

        return false;
    }
}
