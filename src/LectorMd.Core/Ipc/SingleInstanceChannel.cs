using System.IO.Pipes;
using System.Text;

namespace LectorMd.Core.Ipc;

/// <summary>
/// Una sola instancia de la app por usuario. La primera reclama el nombre (mutex) y escucha en un pipe;
/// las siguientes le mandan su archivo (o nada, para que se muestre) y terminan. Así abrir un .md desde
/// el Explorador con la app abierta lo agrega como pestaña en vez de abrir otra ventana.
/// </summary>
public sealed class SingleInstanceChannel : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstanceChannel(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Un mensaje de otra instancia: la ruta de un archivo, o null para "muéstrate".</summary>
    /// <remarks>Llega en un hilo del pool; quien lo atiende pasa al hilo de la interfaz.</remarks>
    public event Action<string?>? MessageReceived;

    /// <summary>Reclama el nombre; null si ya hay otra instancia de este usuario.</summary>
    public static SingleInstanceChannel? TryClaim(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out bool created);
        if (!created)
        {
            mutex.Dispose();
            return null;
        }

        var channel = new SingleInstanceChannel(mutex, PipeName(name));
        _ = Task.Run(channel.ListenAsync);
        return channel;
    }

    /// <summary>Manda un mensaje a la instancia que ya corre. False si no hay ninguna escuchando.</summary>
    public static async Task<bool> SendAsync(string name, string? message, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cancel = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cancel.Token);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteLineAsync(message ?? "");
            await writer.FlushAsync(cancel.Token);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Se libera desde otro hilo que el que lo tomó: al cerrar el identificador queda libre igual.
        }

        _mutex.Dispose();
    }

    // Los pipes son globales a la máquina: el nombre de usuario evita mezclar sesiones de distintas personas.
    private static string PipeName(string name) => $"{name}.{Environment.UserName}";

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(_stop.Token);
                MessageReceived?.Invoke(string.IsNullOrWhiteSpace(line) ? null : line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // La otra instancia se fue a medias: se espera la siguiente.
            }
        }
    }
}
