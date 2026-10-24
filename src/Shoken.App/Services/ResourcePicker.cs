using System.Windows;
using System.Windows.Controls;
using Shoken.Core.Usage;

namespace Shoken.App.Services;

/// <summary>Ventana para elegir el recurso de Speech cuando la cuenta tiene varios.</summary>
internal static class ResourcePicker
{
    public static SpeechResource? Show(Window owner, IReadOnlyList<SpeechResource> resources)
    {
        var list = new ListBox
        {
            ItemsSource = resources.Select(r => $"{r.Name}  ·  {r.Location}  ·  {r.Sku}  ·  {r.Kind}").ToList(),
            SelectedIndex = 0,
            MinHeight = 160,
            Margin = new Thickness(0, 0, 0, 16),
        };
        var accept = new Button { Content = "Usar este", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        var hint = new TextBlock
        {
            Text = "Tu cuenta tiene varios recursos que pueden sintetizar voz. Elige el que tiene la clave que usa la app; " +
                   "se recuerda para la próxima vez.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var content = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        content.Children.Add(hint);
        content.Children.Add(buttons);
        content.Children.Add(list);

        var window = new Window
        {
            Title = "Recurso de Azure Speech",
            Owner = owner,
            Width = 560,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = content,
        };
        accept.Click += (_, _) => window.DialogResult = list.SelectedIndex >= 0;
        list.MouseDoubleClick += (_, _) => window.DialogResult = list.SelectedIndex >= 0;

        return window.ShowDialog() == true ? resources[list.SelectedIndex] : null;
    }
}
