using System.IO;
using System.Windows;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LectorMd.Core.Model;
using LectorMd.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LectorMd.App.Services;

/// <summary>Muestra el documento en un WebView2 y resalta el segmento que se está leyendo.</summary>
internal sealed class DocumentView
{
    private static readonly string[] OpenableExtensions = [".md", ".markdown", ".mdx", ".txt"];

    private readonly WebView2 _webView;
    private readonly AppPaths _paths;
    private CoreWebView2? _core;
    private string? _documentFolder;
    private TaskCompletionSource<bool>? _navigation;
    private string? _expectedUrl;
    private ulong? _expectedNavigationId;
    private int _pageVersion;

    // Cada instancia de la app usa sus propios archivos de página.
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];

    public DocumentView(WebView2 webView, AppPaths paths)
    {
        _webView = webView;
        _paths = paths;
    }

    /// <summary>Doble clic sobre un bloque: valor de su data-seg.</summary>
    public event Action<string>? PlayFromRequested;

    /// <summary>Atajo de teclado o botón dentro de la página ("toggle", "stop", "next", ...).</summary>
    public event Action<string>? CommandRequested;

    public event Action<Uri>? LinkActivated;

    /// <summary>Se soltó un archivo Markdown sobre el documento.</summary>
    public event Action<string>? FileDropped;

    /// <summary>El usuario desplazó el documento: nueva posición vertical en píxeles.</summary>
    public event Action<double>? ScrollChanged;

    /// <summary>Se seleccionaron 1 a 3 palabras: texto, posición bajo la selección (px del visor) y ancla del párrafo.</summary>
    public event Action<string, Point, string?>? SelectionMade;

    /// <summary>La selección se quitó o el documento se desplazó.</summary>
    public event Action? SelectionCleared;

    public static bool IsOpenableFile(string path) =>
        OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync()
    {
        _paths.EnsureCreated();
        WriteAssets();

        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebViewDataDirectory);
        await _webView.EnsureCoreWebView2Async(environment);
        _core = _webView.CoreWebView2;

        CoreWebView2Settings settings = _core.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false; // los atajos los maneja la app
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsZoomControlEnabled = true;
#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        _core.SetVirtualHostNameToFolderMapping(HtmlPages.AppHost, _paths.ViewDirectory, CoreWebView2HostResourceAccessKind.Allow);
        _core.WebMessageReceived += OnWebMessageReceived;
        _core.NavigationStarting += OnNavigationStarting;
        _core.NavigationCompleted += OnNavigationCompleted;
        _core.NewWindowRequested += OnNewWindowRequested;
    }

    /// <summary>Colores del documento: lector.css responde a prefers-color-scheme sin recargar la página.</summary>
    public void SetColorScheme(AppTheme theme)
    {
        if (_core is not null)
        {
            _core.Profile.PreferredColorScheme = theme switch
            {
                AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
                AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
    }

    public Task ShowWelcomeAsync() => NavigateAsync($"bienvenida-{_instance}.html", HtmlPages.Welcome());

    public Task ShowEmptyTabAsync() => NavigateAsync($"vacia-{_instance}.html", HtmlPages.EmptyTab());

    public Task ShowDocumentAsync(SpeechDocument document)
    {
        if (_core is null)
        {
            return Task.CompletedTask;
        }

        string? folder = document.SourcePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(document.SourcePath));
        if (!string.Equals(folder, _documentFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (_documentFolder is not null)
            {
                _core.ClearVirtualHostNameToFolderMapping(HtmlPages.DocumentHost);
            }

            if (folder is not null)
            {
                _core.SetVirtualHostNameToFolderMapping(HtmlPages.DocumentHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            }

            _documentFolder = folder;
        }

        return NavigateAsync($"documento-{_instance}.html", HtmlPages.Document(document, folder is not null));
    }

    /// <param name="follow">Si es false solo marca el segmento, sin desplazar la vista hasta él.</param>
    public Task HighlightAsync(string? anchor, bool follow = true) =>
        RunScriptAsync($"window.lector && window.lector.highlight({JsonSerializer.Serialize(anchor)}, {(follow ? "true" : "false")})");

    public Task ScrollToAsync(string anchor) =>
        RunScriptAsync($"window.lector && window.lector.scrollToAnchor({JsonSerializer.Serialize(anchor)})");

    /// <summary>Lleva un título al borde superior (desde el índice).</summary>
    public Task ScrollToHeadingAsync(string anchor) =>
        RunScriptAsync($"window.lector && window.lector.scrollToHeading({JsonSerializer.Serialize(anchor)})");

    public Task ScrollToYAsync(double y) =>
        RunScriptAsync($"window.lector && window.lector.scrollToY({Math.Max(0, Math.Round(y)).ToString(System.Globalization.CultureInfo.InvariantCulture)})");

    /// <summary>Traduce un enlace https://doc.lector/... a la ruta local dentro de la carpeta del documento.</summary>
    public string? ResolveDocumentLink(Uri uri)
    {
        if (_documentFolder is null || !uri.Host.Equals(HtmlPages.DocumentHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(_documentFolder, relative));
        string root = Path.TrimEndingDirectorySeparator(_documentFolder) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private async Task RunScriptAsync(string script)
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            await _core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // La página se está recargando; el siguiente resaltado lo corrige.
        }
    }

    private async Task NavigateAsync(string fileName, string html)
    {
        if (_core is null)
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(_paths.ViewDirectory, fileName), html, new UTF8Encoding(false));
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = navigation;
        _expectedNavigationId = null;
        _expectedUrl = $"https://{HtmlPages.AppHost}/{fileName}?v={++_pageVersion}";
        _core.Navigate(_expectedUrl);
        await Task.WhenAny(navigation.Task, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Solo cuenta la navegación que pedimos (no una anterior cancelada o reemplazada).
        if (_expectedNavigationId == e.NavigationId)
        {
            _navigation?.TrySetResult(e.IsSuccess);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri))
        {
            e.Cancel = true;
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(HtmlPages.AppHost, StringComparison.OrdinalIgnoreCase))
        {
            if (_expectedUrl is not null && uri.AbsoluteUri == new Uri(_expectedUrl).AbsoluteUri)
            {
                _expectedNavigationId = e.NavigationId;
            }

            return; // nuestras propias páginas
        }

        // Todo lo demás se cancela. Los clics en enlaces ya los maneja lector.js;
        // aquí solo se atiende el caso de soltar un archivo .md sobre el documento.
        e.Cancel = true;
        if (uri.IsFile && IsOpenableFile(uri.LocalPath))
        {
            FileDropped?.Invoke(uri.LocalPath);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri))
        {
            LinkActivated?.Invoke(uri);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "playFrom" when root.TryGetProperty("anchor", out JsonElement anchor) && anchor.GetString() is { } value:
                    PlayFromRequested?.Invoke(value);
                    break;
                case "command" when root.TryGetProperty("command", out JsonElement command) && command.GetString() is { } name:
                    CommandRequested?.Invoke(name);
                    break;
                case "link" when root.TryGetProperty("href", out JsonElement href)
                                 && Uri.TryCreate(href.GetString(), UriKind.Absolute, out Uri? uri):
                    LinkActivated?.Invoke(uri);
                    break;
                // Solo de la página actual: la anterior puede mandar su último desplazamiento ya cambiada la pestaña.
                case "selection" when root.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } selected:
                    var point = new Point(
                        root.TryGetProperty("x", out JsonElement x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0,
                        root.TryGetProperty("y", out JsonElement y2) && y2.ValueKind == JsonValueKind.Number ? y2.GetDouble() : 0);
                    string? block = root.TryGetProperty("anchor", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    SelectionMade?.Invoke(selected, point, block);
                    break;
                case "selectionCleared":
                    SelectionCleared?.Invoke();
                    break;
                case "scroll" when IsCurrentPage(e.Source)
                                   && root.TryGetProperty("y", out JsonElement y) && y.ValueKind == JsonValueKind.Number:
                    ScrollChanged?.Invoke(y.GetDouble());
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Mensaje malformado: se ignora.
        }
    }

    private bool IsCurrentPage(string? source) =>
        _expectedUrl is not null
        && Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
        && uri.AbsoluteUri == new Uri(_expectedUrl).AbsoluteUri;

    private void WriteAssets()
    {
        // Páginas de sesiones anteriores.
        foreach (string old in Directory.EnumerateFiles(_paths.ViewDirectory, "*.html"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(old);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Assembly assembly = typeof(DocumentView).Assembly;
        foreach (string name in new[] { "lector.css", "lector.js" })
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using FileStream file = File.Create(Path.Combine(_paths.ViewDirectory, name));
            stream.CopyTo(file);
        }
    }
}
