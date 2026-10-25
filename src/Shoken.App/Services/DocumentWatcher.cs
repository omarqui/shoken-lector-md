using System.IO;
using System.Windows.Threading;

namespace Shoken.App.Services;

/// <summary>
/// Vigila el archivo de la pestaña activa. Los editores guardan en varias pasadas, o escriben un temporal y lo renombran
/// encima: se avisa una sola vez, cuando pasan 400 ms sin más eventos. Los avisos llegan en el hilo de la ventana.
/// </summary>
internal sealed class DocumentWatcher(Dispatcher dispatcher) : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _timer;
    private string? _path;

    /// <summary>El archivo cambió (o se creó, o otro se renombró con su nombre): su ruta, tras 400 ms sin más eventos.</summary>
    public event Action<string>? Changed;

    /// <summary>Tras esa espera el archivo ya no está (se borró o se renombró a otro nombre).</summary>
    public event Action<string>? Missing;

    /// <summary>Vigila <paramref name="path"/> en lugar del anterior; null deja de vigilar.</summary>
    public void Watch(string? path)
    {
        // El mismo archivo (al recargarlo): se sigue vigilando, sin perder lo que pase mientras.
        if (path is not null && _watcher is not null && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Stop();
        if (path is null)
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is not { } folder || !Directory.Exists(folder))
            {
                return;
            }

            // Sobre la carpeta, filtrando por el nombre: así también se ven el borrado y los renombres hacia él y desde él.
            var watcher = new FileSystemWatcher(folder, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, _) => OnEvent(watcher);
            watcher.Created += (_, _) => OnEvent(watcher);
            watcher.Deleted += (_, _) => OnEvent(watcher);
            watcher.Renamed += (_, _) => OnEvent(watcher);
            watcher.Error += (_, _) => OnEvent(watcher); // se perdieron eventos o la carpeta ya no está: se mira el archivo
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _path = path;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Una carpeta que no se puede vigilar (sin permisos, una unidad que no lo admite): queda F5.
        }
    }

    public void Dispose() => Stop();

    // Llega en un hilo del sistema. Cada evento vuelve a empezar la espera; los de un vigilante anterior se ignoran.
    private void OnEvent(FileSystemWatcher source) =>
        dispatcher.InvokeAsync(() =>
        {
            if (source != _watcher)
            {
                return;
            }

            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = Quiet };
                _timer.Tick += OnQuiet;
            }

            _timer.Stop();
            _timer.Start();
        });

    private void OnQuiet(object? sender, EventArgs e)
    {
        _timer?.Stop();
        if (_path is not { } path)
        {
            return;
        }

        if (File.Exists(path))
        {
            Changed?.Invoke(path);
        }
        else
        {
            Missing?.Invoke(path);
        }
    }

    private void Stop()
    {
        _timer?.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _path = null;
    }
}
