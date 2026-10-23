using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LectorMd.Core.Settings;

namespace LectorMd.App.Services;

/// <summary>Guarda la configuración en JSON; la clave de Azure va cifrada con DPAPI (solo tu usuario de Windows).</summary>
internal sealed class SettingsStore(AppPaths paths)
{
    private static readonly byte[] Entropy = "LectorMd.AzureKey.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(paths.SettingsFile))
            {
                return new AppSettings();
            }

            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(paths.SettingsFile), JsonOptions);
            AppSettings settings = stored?.Settings ?? new AppSettings();
            settings.AzureKey = Unprotect(stored?.AzureKeyProtected);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Write(ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            paths.EnsureCreated();
            var stored = new StoredSettings { Settings = settings, AzureKeyProtected = Protect(settings.AzureKey) };
            File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(ex);
        }
    }

    private static string? Protect(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // creada por otro usuario o en otra máquina
        }
    }

    private sealed class StoredSettings
    {
        public AppSettings? Settings { get; set; }

        public string? AzureKeyProtected { get; set; }
    }
}

internal static class ErrorLog
{
    public static void Write(Exception exception)
    {
        try
        {
            AppPaths paths = AppPaths.Default;
            Directory.CreateDirectory(paths.Root);
            File.AppendAllText(paths.ErrorLogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
