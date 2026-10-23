using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core.Ipc;
using LectorMd.Core.Settings;

namespace LectorMd.App;

public partial class App : Application
{
    private const string InstanceName = "LectorMd.Instancia";

    private SingleInstanceChannel? _instance;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Permite "Abrir con > Lector MD" o arrastrar un .md sobre el ejecutable.
        string? file = e.Args.FirstOrDefault(File.Exists) is { } arg ? Path.GetFullPath(arg) : null;

        _instance = SingleInstanceChannel.TryClaim(InstanceName);
        if (_instance is null)
        {
            // Ya corre otra: que abra el archivo como pestaña (o se muestre) y esta termina.
            // Esta instancia la abrió el usuario, así que puede ceder el primer plano a la otra.
            AllowSetForegroundWindow(AsfwAny);
            if (await SingleInstanceChannel.SendAsync(InstanceName, file, TimeSpan.FromSeconds(5)))
            {
                Shutdown();
                return;
            }

            // La otra no respondió (se está cerrando o se colgó): mejor abrir esta que no abrir nada.
        }

        var window = new MainWindow(file);
        MainWindow = window;
        window.Show();

        if (_instance is not null)
        {
            // Lo que otras instancias mandaron mientras arrancaba quedó guardado: se entrega cuando las pestañas
            // ya se restauraron (si no, se abriría antes y se mezclaría con la pestaña activa guardada).
            await window.Ready;
            _instance.AttachHandler(message => Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await window.BringToFrontAsync(message);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write(ex);
                }
            }));
        }
    }

    private void OnExit(object sender, ExitEventArgs e) => _instance?.Dispose();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        MessageBox.Show(
            $"Ocurrió un error inesperado:\n\n{e.Exception.Message}\n\nSe guardó el detalle en {AppPaths.Default.ErrorLogFile}",
            "Lector MD", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
