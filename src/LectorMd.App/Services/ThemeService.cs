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

    /// <summary>Tema de color: cada uno con su versión clara y oscura, según <see cref="Current"/>.</summary>
    public static ThemePalette Palette { get; private set; } = ThemePalette.Paper;

    /// <summary>Variables CSS del documento para lo que se ve ahora (tema de color y modo).</summary>
    public static IReadOnlyDictionary<string, string> DocumentVariables =>
        ThemePalettes.DocumentVariables(ThemePalettes.Get(Palette, IsDark), IsDark);

    public static void Apply(AppTheme theme, ThemePalette palette)
    {
        Current = theme;
        Palette = palette;
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        ApplyPalette(IsDark);
    }

    /// <summary>
    /// Colores propios del diseño (pestañas, barra de comandos, reproductor) del tema de color elegido; vienen de
    /// <see cref="ThemePalettes"/>, como los del documento. Se usan como DynamicResource, así que cambian al
    /// alternar el tema sin recrear la ventana.
    /// </summary>
    private static void ApplyPalette(bool dark)
    {
        PaletteColors c = ThemePalettes.Get(Palette, dark);
        ResourceDictionary resources = Application.Current.Resources;
        void Set(string key, string color) => resources[key] = Frozen((Color)ColorConverter.ConvertFromString(color));

        Set("LectorBackgroundBrush", c.Background);
        Set("LectorSurfaceBrush", c.Surface);
        Set("LectorSurface2Brush", c.Surface2);
        Set("LectorBorderBrush", c.Border);
        Set("LectorTextBrush", c.Text);
        Set("LectorText2Brush", c.Text2);
        Set("LectorAccentBrush", c.Accent);
        Set("LectorPastedBrush", c.Pasted); // ícono de las pestañas con texto pegado
        Set("LectorOnAccentBrush", c.OnAccent);
        Set("LectorCurrentBrush", c.Current);
        Set("LectorCurrentTextBrush", c.CurrentText);
        Set("LectorTrackBrush", c.Track);
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
