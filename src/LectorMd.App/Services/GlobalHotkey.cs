using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LectorMd.App.Services;

/// <summary>Atajo de teclado de todo Windows (funciona aunque la app esté en la bandeja o en otra ventana).</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly int _id;
    private readonly Action _pressed;

    private GlobalHotkey(HwndSource source, int id, Action pressed)
    {
        _source = source;
        _id = id;
        _pressed = pressed;
        _source.AddHook(Hook);
    }

    /// <summary>Registra el atajo sobre la ventana (aunque esté oculta). Null si otra app ya lo tiene.</summary>
    public static GlobalHotkey? TryRegister(Window window, ModifierKeys modifiers, Key key, Action pressed)
    {
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        if (HwndSource.FromHwnd(handle) is not { } source)
        {
            return null;
        }

        uint mods = ModNoRepeat
                    | (modifiers.HasFlag(ModifierKeys.Alt) ? ModAlt : 0)
                    | (modifiers.HasFlag(ModifierKeys.Control) ? ModControl : 0)
                    | (modifiers.HasFlag(ModifierKeys.Shift) ? ModShift : 0);
        const int id = 0x4C4D; // "LM"
        return RegisterHotKey(handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key))
            ? new GlobalHotkey(source, id, pressed)
            : null;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        UnregisterHotKey(_source.Handle, _id);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
