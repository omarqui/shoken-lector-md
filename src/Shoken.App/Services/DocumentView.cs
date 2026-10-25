using System.IO;
using System.Windows;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Shoken.Core.Desktop;
using Shoken.Core.Model;
using Shoken.Core.Notes;
using Shoken.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Shoken.App.Services;

/// <summary>Muestra el documento en un WebView2 y resalta el segmento que se está leyendo.</summary>
internal sealed class DocumentView
{
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

    /// <summary>Se soltaron archivos sobre el documento: sus rutas, incluso las que no son Markdown.</summary>
    public event Action<IReadOnlyList<string>>? FilesDropped;

    /// <summary>Entraron al documento archivos arrastrados (la página no sabe cuáles).</summary>
    public event Action? FilesDragging;

    /// <summary>El usuario desplazó el documento: nueva posición vertical en píxeles.</summary>
    public event Action<double>? ScrollChanged;

    /// <summary>
    /// Se seleccionó texto del documento: texto, posición bajo la selección (px del visor), ancla del párrafo y si
    /// son 1 a 3 palabras (entonces también se puede editar su pronunciación).
    /// </summary>
    public event Action<string, Point, string?, bool>? SelectionMade;

    /// <summary>La selección se quitó o el documento se desplazó.</summary>
    public event Action? SelectionCleared;

    /// <summary>Cambió la búsqueda (Ctrl+F): resultados por bloque (su data-seg), o null si se cerró o está vacía.</summary>
    public event Action<IReadOnlyDictionary<string, int>?>? FindChanged;

    /// <summary>Algo que se hizo con los apuntes en la página (crear, editar, borrar, copiar...). Solo de la página actual.</summary>
    public event Action<NoteRequest>? NoteRequested;

    /// <summary>La demo de entonación pide algo de «Tu voz» ("voice", "generate", "cancel"). Solo de la página actual.</summary>
    public event Action<string>? DemoRequested;

    public static bool IsOpenableFile(string path) => DraggedFiles.IsOpenable(path);

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

    /// <summary>Claro u oscuro para lo que el navegador dibuja solo (barras de desplazamiento, controles).</summary>
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

    /// <summary>
    /// Colores del tema elegido (variables de lector.css, sin el "--"): cambian la página abierta sin recargarla y
    /// se usan en las que se abran después.
    /// </summary>
    public Task SetColorsAsync(IReadOnlyDictionary<string, string> colors)
    {
        _colors = colors;
        return RunScriptAsync($"window.lector && window.lector.setColors({JsonSerializer.Serialize(colors)})");
    }

    private IReadOnlyDictionary<string, string>? _colors;

    private string? ThemeCss => _colors is null ? null : ":root{" + string.Concat(_colors.Select(c => $"--{c.Key}:{c.Value};")) + "}";

    public Task ShowWelcomeAsync() => NavigateAsync($"bienvenida-{_instance}.html", HtmlPages.Welcome(ThemeCss));

    public Task ShowEmptyTabAsync() => NavigateAsync($"vacia-{_instance}.html", HtmlPages.EmptyTab(ThemeCss));

    /// <summary>Prefijo de los archivos de audio de «Tu voz» en la carpeta de la vista.</summary>
    private const string DemoVoicePrefix = "demo-tuvoz-";

    /// <summary>La demo de entonación, con los datos que lee demo.js.</summary>
    public Task ShowDemoAsync(SpeechDocument document, string dataJson) =>
        NavigateAsync($"demo-{_instance}.html", HtmlPages.Demo(document, dataJson, ThemeCss));

    private static readonly JsonSerializerOptions DemoJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Llama a una función de la demo (window.shokenDemo) con un argumento en JSON, si la página es la demo.</summary>
    public Task CallDemoAsync(string function, object? argument = null) =>
        RunScriptAsync($"window.shokenDemo && window.shokenDemo.{function}({(argument is null ? "" : JsonSerializer.Serialize(argument, DemoJson))})");

    /// <summary>
    /// Copia un audio generado a la carpeta que sirve app.lector para que la demo lo reproduzca; devuelve su URL.
    /// Cada versión lleva un nombre nuevo para que el navegador no reproduzca la anterior.
    /// </summary>
    public string PublishDemoAudio(string name, byte[] audio, string extension)
    {
        string file = $"{DemoVoicePrefix}{name}-{++_demoAudioVersion}{extension}";
        File.WriteAllBytes(Path.Combine(_paths.ViewDirectory, file), audio);
        return $"https://{HtmlPages.AppHost}/{file}";
    }

    private int _demoAudioVersion;

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

        return NavigateAsync($"documento-{_instance}.html", HtmlPages.Document(document, folder is not null, ThemeCss));
    }

    /// <param name="follow">Si es false solo marca el segmento, sin desplazar la vista hasta él.</param>
    public Task HighlightAsync(string? anchor, bool follow = true) =>
        RunScriptAsync($"window.lector && window.lector.highlight({JsonSerializer.Serialize(anchor)}, {(follow ? "true" : "false")})");

    public Task ScrollToAsync(string anchor) =>
        RunScriptAsync($"window.lector && window.lector.scrollToAnchor({JsonSerializer.Serialize(anchor)})");

    /// <summary>Lleva un título al borde superior (desde el índice).</summary>
    public Task ScrollToHeadingAsync(string anchor) =>
        RunScriptAsync($"window.lector && window.lector.scrollToHeading({JsonSerializer.Serialize(anchor)})");

    /// <summary>Abre la barra de búsqueda del documento (o la enfoca si ya está abierta).</summary>
    public Task OpenFindAsync()
    {
        _webView.Focus(); // el cuadro de búsqueda vive en la página: necesita el foco del WebView2
        return RunScriptAsync("window.lector && window.lector.find.open()");
    }

    /// <summary>
    /// Los apuntes del documento abierto: la página ubica cada cita, la subraya y pone su tarjeta al lado.
    /// <paramref name="editId"/> abre esa tarjeta para escribir (recién creada).
    /// </summary>
    public Task SetNotesAsync(IEnumerable<DocumentNote> notes, bool visible, string? editId = null)
    {
        var items = notes.Select(n => new { id = n.Id, quote = n.Quote, kind = n.Kind.ToString(), text = n.Text, resolved = n.Resolved, line = n.Line });
        if (editId is not null)
        {
            _webView.Focus(); // el cuadro de la nota vive en la página: necesita el foco del WebView2
        }

        return RunScriptAsync($"window.lector && window.lector.notes.set({JsonSerializer.Serialize(items)}, {(visible ? "true" : "false")}, {JsonSerializer.Serialize(editId)})");
    }

    public Task SetNotesVisibleAsync(bool visible) =>
        RunScriptAsync($"window.lector && window.lector.notes.setVisible({(visible ? "true" : "false")})");

    /// <summary>Pide un apunte para lo seleccionado (o lo último que se seleccionó); false si no hay selección.</summary>
    public async Task<bool> CreateNoteFromSelectionAsync()
    {
        _webView.Focus();
        return await EvaluateAsync("window.lector ? window.lector.notes.createFromSelection() : false") == "true";
    }

    /// <summary>Pide un apunte para un párrafo entero (su data-seg).</summary>
    public Task CreateNoteForSegmentAsync(string anchor)
    {
        _webView.Focus();
        return RunScriptAsync($"window.lector && window.lector.notes.createForSegment({JsonSerializer.Serialize(anchor)})");
    }

    /// <summary>Ctrl+Alt+M: la selección; sin selección la página avisa (<see cref="NoteRequest.NoSelection"/>).</summary>
    public Task CreateNoteHereAsync()
    {
        _webView.Focus();
        return RunScriptAsync("window.lector && window.lector.notes.createHere()");
    }

    /// <summary>El apunte siguiente (1) o anterior (-1) en el texto: lleva la vista a su cita y marca su tarjeta.</summary>
    public Task GoToNoteAsync(int step)
    {
        _webView.Focus();
        return RunScriptAsync($"window.lector && window.lector.notes.go({Math.Sign(step)})");
    }

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

    private Task RunScriptAsync(string script) => EvaluateAsync(script);

    /// <summary>Corre el script y devuelve su resultado en JSON ("null" si la página no estaba lista).</summary>
    private async Task<string> EvaluateAsync(string script)
    {
        if (_core is null)
        {
            return "null";
        }

        try
        {
            return await _core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // La página se está recargando; el siguiente resaltado lo corrige.
            return "null";
        }
    }

    private async Task NavigateAsync(string fileName, string html)
    {
        if (_core is null)
        {
            return;
        }

        // Desde ya, lo que mande la página anterior (un apunte editado, su desplazamiento) no es de la nueva.
        _expectedUrl = null;
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

        // Todo lo demás se cancela. Los clics en enlaces y los archivos soltados ya los maneja lector.js; esto solo
        // atiende un archivo soltado antes de que cargara la página (el navegador navega a él).
        e.Cancel = true;
        if (uri.IsFile)
        {
            FilesDropped?.Invoke([uri.LocalPath]);
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
                // La página no ve rutas; WebView2 adjunta los archivos soltados, con su ruta, al mensaje.
                case "drop" when e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToList() is { Count: > 0 } files:
                    FilesDropped?.Invoke(files);
                    break;
                case "dragFiles":
                    FilesDragging?.Invoke();
                    break;
                // Solo de la página actual: la anterior puede mandar su último desplazamiento ya cambiada la pestaña.
                case "selection" when root.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } selected:
                    var point = new Point(
                        root.TryGetProperty("x", out JsonElement x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0,
                        root.TryGetProperty("y", out JsonElement y2) && y2.ValueKind == JsonValueKind.Number ? y2.GetDouble() : 0);
                    string? block = root.TryGetProperty("anchor", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                    SelectionMade?.Invoke(selected, point, block, root.TryGetProperty("short", out JsonElement s) && s.ValueKind == JsonValueKind.True);
                    break;
                case { } note when note.StartsWith("note", StringComparison.Ordinal) && IsCurrentPage(e.Source)
                                   && ReadNoteRequest(note, root) is { } request:
                    NoteRequested?.Invoke(request);
                    break;
                case "selectionCleared":
                    SelectionCleared?.Invoke();
                    break;
                case "find" when IsCurrentPage(e.Source):
                    FindChanged?.Invoke(ReadFindCounts(root));
                    break;
                case "scroll" when IsCurrentPage(e.Source)
                                   && root.TryGetProperty("y", out JsonElement y) && y.ValueKind == JsonValueKind.Number:
                    ScrollChanged?.Invoke(y.GetDouble());
                    break;
                case "demo" when IsCurrentPage(e.Source)
                                 && root.TryGetProperty("action", out JsonElement action) && action.GetString() is { } demoAction:
                    DemoRequested?.Invoke(demoAction);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Mensaje malformado: se ignora.
        }
    }

    private static Dictionary<string, int>? ReadFindCounts(JsonElement root)
    {
        if (!root.TryGetProperty("active", out JsonElement active) || active.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("counts", out JsonElement counts) || counts.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty block in counts.EnumerateObject())
        {
            if (block.Value.ValueKind == JsonValueKind.Number && block.Value.TryGetInt32(out int matches) && matches > 0)
            {
                result[block.Name] = matches;
            }
        }

        return result;
    }

    private static NoteRequest? ReadNoteRequest(string type, JsonElement root)
    {
        string? Text(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool? Flag(string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

        string? id = Text(root, "id");
        switch (type)
        {
            case "noteCreate" when Text(root, "quote") is { Length: > 0 } quote && Text(root, "startSeg") is { } start:
                return new NoteRequest.Create(quote, start, Text(root, "endSeg") ?? start);
            case "noteUpdate" when id is not null:
                NoteKind? kind = Enum.TryParse(Text(root, "kind"), out NoteKind parsed) ? parsed : null;
                return new NoteRequest.Update(id, Text(root, "text"), kind, Flag("resolved"));
            case "noteDelete" when id is not null:
                return new NoteRequest.Delete(id, Flag("silent") == true);
            case "noteRestore" when id is not null:
                return new NoteRequest.Restore(id);
            case "notesAnchored" when root.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array:
                return new NoteRequest.Anchored(results.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.Object && Text(r, "id") is not null)
                    .Select(r => new NoteAnchor(Text(r, "id")!, r.TryGetProperty("found", out JsonElement f) && f.ValueKind == JsonValueKind.True,
                        Text(r, "startSeg"), Text(r, "endSeg")))
                    .ToList());
            case "notesExport":
                return new NoteRequest.Export();
            case "notesClearResolved":
                return new NoteRequest.ClearResolved();
            case "noteNoSelection":
                return new NoteRequest.NoSelection();
            default:
                return null;
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

        // El audio de «Tu voz» de la demo se genera de nuevo en cada sesión (y sale de la caché).
        foreach (string old in Directory.EnumerateFiles(_paths.ViewDirectory, DemoVoicePrefix + "*"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        Assembly assembly = typeof(DocumentView).Assembly;
        foreach (string name in new[] { "lector.css", "lector.js", "mermaid.min.js", "demo.css", "demo.js", "demo-con.mp3", "demo-sin.mp3" })
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

/// <summary>Lo que pide la página sobre los apuntes. La app es la dueña: los guarda y arma la exportación.</summary>
internal abstract record NoteRequest
{
    /// <summary>Apunte nuevo sobre <paramref name="Quote"/>, que empieza y termina en esos bloques (data-seg).</summary>
    public sealed record Create(string Quote, string StartAnchor, string EndAnchor) : NoteRequest;

    /// <summary>Cambió la nota, el tipo o "resuelto" (null = no cambió).</summary>
    public sealed record Update(string Id, string? Text, NoteKind? Kind, bool? Resolved) : NoteRequest;

    /// <param name="Silent">Un apunte recién creado que se descartó vacío: no se ofrece deshacer.</param>
    public sealed record Delete(string Id, bool Silent) : NoteRequest;

    public sealed record Restore(string Id) : NoteRequest;

    /// <summary>Dónde se encontró cada cita al abrir el documento.</summary>
    public sealed record Anchored(IReadOnlyList<NoteAnchor> Results) : NoteRequest;

    public sealed record Export : NoteRequest;

    public sealed record ClearResolved : NoteRequest;

    /// <summary>Ctrl+Alt+M sin texto seleccionado.</summary>
    public sealed record NoSelection : NoteRequest;
}

/// <summary>Una cita ubicada en la página (o no: <paramref name="Found"/> = false).</summary>
internal sealed record NoteAnchor(string Id, bool Found, string? StartAnchor, string? EndAnchor);
