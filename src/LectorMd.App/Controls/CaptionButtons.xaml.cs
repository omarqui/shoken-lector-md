using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace LectorMd.App.Controls;

/// <summary>Minimizar, maximizar/restaurar y cerrar, para la barra de título propia (las pestañas viven ahí).</summary>
public partial class CaptionButtons : UserControl
{
    private Window? _window;

    public CaptionButtons()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.StateChanged += (_, _) => UpdateMaximizeButton();
                UpdateMaximizeButton();
            }
        };
    }

    private void UpdateMaximizeButton()
    {
        bool maximized = _window?.WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        string label = maximized ? "Restaurar" : "Maximizar";
        MaximizeButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeButton, label);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            SystemCommands.MinimizeWindow(_window);
        }
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(_window);
        }
        else
        {
            SystemCommands.MaximizeWindow(_window);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            SystemCommands.CloseWindow(_window);
        }
    }
}
