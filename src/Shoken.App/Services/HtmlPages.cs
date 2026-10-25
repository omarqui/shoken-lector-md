using System.Net;
using System.Text;
using Shoken.Core.Model;

namespace Shoken.App.Services;

/// <summary>Páginas que se muestran en el WebView2.</summary>
internal static class HtmlPages
{
    /// <summary>Host virtual de los recursos de la app (CSS, JS, páginas generadas).</summary>
    public const string AppHost = "app.lector";

    /// <summary>Host virtual de la carpeta del documento abierto (imágenes relativas).</summary>
    public const string DocumentHost = "doc.lector";

    // Solo se ejecuta nuestro script: un <script> dentro del Markdown queda bloqueado. El audio de app.lector es el
    // de la demo de entonación.
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src https://app.lector; " +
        "style-src https://app.lector 'unsafe-inline'; " +
        "img-src https://doc.lector https: data:; " +
        "media-src https://doc.lector https://app.lector; " +
        "font-src https://app.lector";

    /// <summary>Nombre de la pestaña de la demo de entonación (y título de su página).</summary>
    public const string DemoTitle = "Así entona Shoken";

    public static string Document(SpeechDocument document, bool hasDocumentFolder, string? themeCss) =>
        Page(document.Title, $"<main class=\"doc\">{document.HtmlBody}</main>", hasDocumentFolder, themeCss);

    public static string Welcome(string? themeCss) => Page("Shoken", WelcomeBody, hasDocumentFolder: false, themeCss);

    public static string EmptyTab(string? themeCss) => Page("Nueva pestaña", EmptyTabBody, hasDocumentFolder: false, themeCss);

    /// <summary>
    /// La demo de entonación: el documento de la demo tal como se ve en Shoken y, alrededor, lo que arma demo.js
    /// (reproductor, líneas de tiempo y notas) con los datos de <paramref name="dataJson"/>.
    /// </summary>
    public static string Demo(SpeechDocument document, string dataJson, string? themeCss)
    {
        // Un <script type="application/json"> no se ejecuta (la CSP no lo bloquea); "</" escapado para que no lo cierre.
        string body = DemoBody.Replace("{documento}", document.HtmlBody, StringComparison.Ordinal) +
                      $"<script type=\"application/json\" id=\"demo-data\">{dataJson.Replace("</", "<\\/", StringComparison.Ordinal)}</script>";
        return Page(DemoTitle, body, hasDocumentFolder: false, themeCss, extraStyle: "demo.css", extraScript: "demo.js");
    }

    /// <param name="themeCss">Variables de color del tema elegido (pisan los valores por omisión de lector.css).</param>
    /// <param name="extraStyle">Hoja de estilos de app.lector que va después de lector.css, o null.</param>
    /// <param name="extraScript">Script de app.lector que corre después de lector.js, o null.</param>
    private static string Page(string title, string body, bool hasDocumentFolder, string? themeCss,
        string? extraStyle = null, string? extraScript = null)
    {
        var html = new StringBuilder(body.Length + 1024);
        html.Append("<!doctype html><html lang=\"es-MX\"><head><meta charset=\"utf-8\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">");
        if (hasDocumentFolder)
        {
            html.Append("<base href=\"https://").Append(DocumentHost).Append("/\">");
        }

        html.Append("<link rel=\"stylesheet\" href=\"https://").Append(AppHost).Append("/lector.css\">");
        if (extraStyle is not null)
        {
            html.Append("<link rel=\"stylesheet\" href=\"https://").Append(AppHost).Append('/').Append(extraStyle).Append("\">");
        }

        if (themeCss is not null)
        {
            html.Append("<style>").Append(themeCss).Append("</style>");
        }

        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title></head><body>");
        html.Append(body);
        html.Append("<script src=\"https://").Append(AppHost).Append("/lector.js\"></script>");
        if (extraScript is not null)
        {
            html.Append("<script src=\"https://").Append(AppHost).Append('/').Append(extraScript).Append("\"></script>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    // Los textos que dependen de los datos (duraciones, voz, costo) los completa demo.js.
    private const string DemoBody = """
        <main class="demo">
          <header class="demo-intro">
            <p class="demo-eyebrow">Demo de entonación</p>
            <h1 class="demo-title">Así entona Shoken</h1>
            <p class="demo-lead">El mismo texto, leído dos veces por la misma voz. <b class="demo-con">Con Shoken</b> usa el formato para poner pausas, cerrar cada título con un punto y cambiar el tono de títulos y negritas. <b class="demo-sin">Sin entonación</b> lee las mismas palabras de corrido. Cambia de versión mientras suena: la otra sigue en la misma parte.</p>
          </header>
          <div class="demo-bar" role="toolbar" aria-label="Reproductor de la demo">
            <div class="demo-controls">
              <button type="button" id="demo-play" class="demo-play" aria-label="Reproducir">
                <svg viewBox="0 0 24 24" aria-hidden="true"><path id="demo-play-icon" d="M8 5v14l11-7z"/></svg><span id="demo-play-text">Reproducir</span>
              </button>
              <div class="demo-switch" role="group" aria-label="Versión">
                <button type="button" data-version="con" aria-pressed="true">Con Shoken</button>
                <button type="button" data-version="sin" aria-pressed="false">Sin entonación</button>
              </div>
              <span class="demo-clock" id="demo-clock">0:00</span>
              <div class="demo-switch demo-small" role="group" aria-label="Velocidad">
                <button type="button" data-speed="1" aria-pressed="true">1×</button>
                <button type="button" data-speed="1.25" aria-pressed="false">1.25×</button>
              </div>
              <div class="demo-switch demo-small" role="group" aria-label="Voz">
                <button type="button" data-voice="recorded" aria-pressed="true">Dalia (grabada)</button>
                <button type="button" data-voice="mine" id="demo-mine" aria-pressed="false">Tu voz</button>
              </div>
            </div>
            <div class="demo-msg" id="demo-msg" role="status" hidden></div>
          </div>
          <section class="demo-lanes" id="demo-lanes" aria-label="Las dos lecturas, a la misma escala"></section>
          <div class="demo-legend" aria-hidden="true">
            <span><i class="demo-sw-h"></i>Título</span>
            <span><i class="demo-sw-b"></i>Párrafo, lista o cita</span>
            <span><i class="demo-sw-p"></i>Pausa que pide Shoken</span>
          </div>
          <div class="demo-body">
            <article class="doc demo-doc" id="demo-doc">{documento}</article>
            <aside class="demo-notes" id="demo-notes" aria-label="Lo que hace Shoken en cada parte"></aside>
          </div>
          <p class="demo-cost" id="demo-cost"></p>
        </main>
        <audio id="demo-audio-con" preload="auto"></audio>
        <audio id="demo-audio-sin" preload="auto"></audio>
        """;

    private const string WelcomeBody = """
        <section class="welcome">
          <svg class="welcome-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5H4z"/>
            <path d="M15.5 9a4 4 0 0 1 0 6"/>
            <path d="M18 6.5a7.5 7.5 0 0 1 0 11"/>
          </svg>
          <h1>Shoken</h1>
          <p class="lead">Abre un archivo Markdown y lo leo en voz alta en español de México, con pausas y entonación según su formato.</p>
          <div class="welcome-actions">
            <button type="button" data-command="open">Abrir archivo…</button>
            <button type="button" class="secondary" data-command="demo">Escucha cómo entona</button>
          </div>
          <p class="hint">También puedes arrastrarlo a esta ventana, o pegar (<kbd>Ctrl</kbd> + <kbd>V</kbd>) su ruta o un texto Markdown.</p>
          <dl class="keys">
            <dt><kbd>Espacio</kbd></dt><dd>Leer o pausar</dd>
            <dt>Doble clic</dt><dd>Leer desde ese párrafo</dd>
            <dt><kbd>Ctrl</kbd> + <kbd>→</kbd> / <kbd>←</kbd></dt><dd>Sección siguiente o anterior</dd>
            <dt><kbd>Esc</kbd></dt><dd>Detener</dd>
            <dt><kbd>Ctrl</kbd> + <kbd>T</kbd> / <kbd>W</kbd></dt><dd>Pestaña nueva o cerrar pestaña</dd>
            <dt><kbd>Ctrl</kbd> + <kbd>Tab</kbd></dt><dd>Pestaña siguiente</dd>
            <dt><kbd>Ctrl</kbd> + <kbd>B</kbd></dt><dd>Mostrar u ocultar el índice</dd>
            <dt><kbd>Ctrl</kbd> + <kbd>F</kbd></dt><dd>Buscar en el documento</dd>
            <dt><kbd>F5</kbd></dt><dd>Recargar el documento</dd>
          </dl>
        </section>
        """;

    private const string EmptyTabBody = """
        <section class="welcome">
          <h1>Nueva pestaña</h1>
          <p class="lead">Pega aquí (<kbd>Ctrl</kbd> + <kbd>V</kbd>) un texto Markdown para escucharlo, o la ruta de un archivo .md para abrirlo.</p>
          <button type="button" data-command="paste">Pegar del portapapeles</button>
          <p class="hint">También puedes abrir un archivo (<kbd>Ctrl</kbd> + <kbd>O</kbd>) o arrastrarlo a la ventana.</p>
        </section>
        """;
}
