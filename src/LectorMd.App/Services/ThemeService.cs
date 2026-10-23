using System.Windows;
using LectorMd.Core.Settings;
using Microsoft.Win32;

namespace LectorMd.App.Services;

/// <summary>Tema de la ventana (tema Fluent de WPF): claro, oscuro o el del sistema.</summary>
internal static class ThemeService
{
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
