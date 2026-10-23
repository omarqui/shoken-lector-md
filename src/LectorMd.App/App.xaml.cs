using System.Windows;
using System.Windows.Threading;
using LectorMd.App.Services;
using LectorMd.Core.Settings;

namespace LectorMd.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Permite "Abrir con > Lector MD" o arrastrar un .md sobre el ejecutable.
        string? file = e.Args.FirstOrDefault(File.Exists);
        var window = new MainWindow(file);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        MessageBox.Show(
            $"Ocurrió un error inesperado:\n\n{e.Exception.Message}\n\nSe guardó el detalle en {AppPaths.Default.ErrorLogFile}",
            "Lector MD", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
