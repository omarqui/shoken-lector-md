using System.Windows;
using System.Windows.Media;
using LectorMd.Core.Settings;
using Microsoft.Win32;

namespace LectorMd.App.Services;

/// <summary>Tema de la ventana (tema Fluent de WPF): claro, oscuro o el del sistema.</summary>
internal static class ThemeService
{
    private static bool _watching;

    public static AppTheme Current { get; private set; } = AppTheme.System;

    /// <summary>True si lo que se ve es oscuro (con "Sistema", según la configuración de Windows).</summary>
    public static bool IsDark => Current switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemUsesDark(),
    };

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        ApplyPalette(IsDark);
    }

    /// <summary>
    /// Colores propios del diseño (pestañas, barra de comandos, reproductor), los mismos de la propuesta D.
    /// Se usan como DynamicResource, así que cambian al alternar el tema sin recrear la ventana.
    /// </summary>
    private static void ApplyPalette(bool dark)
    {
        ResourceDictionary resources = Application.Current.Resources;
        void Set(string key, string light, string darkColor) =>
            resources[key] = Frozen((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));

        Set("LectorBackgroundBrush", "#F3F3F3", "#202020");
        Set("LectorSurfaceBrush", "#FFFFFF", "#2B2B2B");
        Set("LectorSurface2Brush", "#F0F0F0", "#363636");
        Set("LectorBorderBrush", "#E5E5E5", "#3D3D3D");
        Set("LectorTextBrush", "#1B1B1B", "#FFFFFF");
        Set("LectorText2Brush", "#5D5D5D", "#C8C8C8");
        Set("LectorAccentBrush", "#005FB8", "#60CDFF");
        Set("LectorPastedBrush", "#8250DF", "#C297FF"); // ícono de las pestañas con texto pegado
        Set("LectorOnAccentBrush", "#FFFFFF", "#00344D");
        Set("LectorCurrentBrush", "#DCE9F7", "#1E3A4C");
        Set("LectorCurrentTextBrush", "#003D7A", "#99EBFF");
        Set("LectorTrackBrush", "#E0E0E0", "#4A4A4A");
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Cambió lo que se ve porque Windows cambió de tema (solo con "Sistema").</summary>
    public static event Action? Changed;

    /// <summary>
    /// Con "Sistema", sigue los cambios de tema de Windows con la app abierta. El tema Fluent y el documento
    /// lo hacen solos; los colores propios (Lector*Brush) hay que volver a aplicarlos, o la ventana queda a medias.
    /// </summary>
    public static void WatchSystemTheme()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color && Current == AppTheme.System)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ApplyPalette(IsDark);
                    Changed?.Invoke();
                });
            }
        };
    }

    /// <summary>El tema opuesto al que se ve: el botón alterna claro y oscuro.</summary>
    public static AppTheme Toggled() => IsDark ? AppTheme.Light : AppTheme.Dark;

    private static bool SystemUsesDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
