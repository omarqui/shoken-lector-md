using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Shoken.App.Services;

/// <summary>
/// Pone a Shoken en "Abrir con" de los .md y .markdown cuando corre sin empaquetar (dotnet run, publicar\). Empaquetada no
/// hace falta: lo declara el manifiesto MSIX y Windows lo quita al desinstalar.
/// </summary>
/// <remarks>
/// Solo para el usuario actual (HKCU, sin pedir permisos), apuntando al ejecutable que corre: si se mueve, se corrige al
/// abrirlo. No la vuelve la app predeterminada; Windows no deja que una app se ponga sola, lo elige el usuario en
/// "Abrir con > Elegir otra aplicación".
/// </remarks>
internal static class FileAssociation
{
    private const string ProgId = "Shoken.Markdown";
    private static readonly string[] Extensions = [".md", ".markdown"];

    public static void Register(string exePath)
    {
        string command = $"\"{exePath}\" \"%1\"";
        using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        if (IsRegistered(classes, command))
        {
            return;
        }

        using (RegistryKey progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue(null, "Documento Markdown");
            using RegistryKey icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exePath}\",0");
            using RegistryKey open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        foreach (string extension in Extensions)
        {
            using RegistryKey openWith = classes.CreateSubKey($@"{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Sin este aviso, el Explorador sigue mostrando el "Abrir con" anterior hasta reiniciar la sesión.
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool IsRegistered(RegistryKey classes, string command)
    {
        using RegistryKey? open = classes.OpenSubKey($@"{ProgId}\shell\open\command");
        return open?.GetValue(null) as string == command
               && Extensions.All(extension =>
               {
                   using RegistryKey? openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids");
                   return openWith?.GetValueNames().Contains(ProgId) == true;
               });
    }

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
