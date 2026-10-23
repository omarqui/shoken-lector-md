using System.Net;
using System.Text;
using LectorMd.Core.Model;

namespace LectorMd.App.Services;

/// <summary>Páginas que se muestran en el WebView2.</summary>
internal static class HtmlPages
{
    /// <summary>Host virtual de los recursos de la app (CSS, JS, páginas generadas).</summary>
    public const string AppHost = "app.lector";

    /// <summary>Host virtual de la carpeta del documento abierto (imágenes relativas).</summary>
    public const string DocumentHost = "doc.lector";

    // Solo se ejecuta nuestro script: un <script> dentro del Markdown queda bloqueado.
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src https://app.lector; " +
        "style-src https://app.lector 'unsafe-inline'; " +
        "img-src https://doc.lector https: data:; " +
        "media-src https://doc.lector; " +
        "font-src https://app.lector";

    public static string Document(SpeechDocument document, bool hasDocumentFolder) =>
        Page(document.Title, $"<main class=\"doc\">{document.HtmlBody}</main>", hasDocumentFolder);

    public static string Welcome() => Page("Lector MD", WelcomeBody, hasDocumentFolder: false);

    public static string EmptyTab() => Page("Nueva pestaña", EmptyTabBody, hasDocumentFolder: false);

    private static string Page(string title, string body, bool hasDocumentFolder)
    {
        var html = new StringBuilder(body.Length + 1024);
        html.Append("<!doctype html><html lang=\"es-MX\"><head><meta charset=\"utf-8\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy).Append("\">");
        if (hasDocumentFolder)
        {
            html.Append("<base href=\"https://").Append(DocumentHost).Append("/\">");
        }

        html.Append("<link rel=\"stylesheet\" href=\"https://").Append(AppHost).Append("/lector.css\">");
        html.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title></head><body>");
        html.Append(body);
        html.Append("<script src=\"https://").Append(AppHost).Append("/lector.js\"></script></body></html>");
        return html.ToString();
    }

    private const string WelcomeBody = """
        <section class="welcome">
          <svg class="welcome-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M4 9.5v5h3.5L12 18.5v-13L7.5 9.5H4z"/>
            <path d="M15.5 9a4 4 0 0 1 0 6"/>
            <path d="M18 6.5a7.5 7.5 0 0 1 0 11"/>
          </svg>
          <h1>Lector MD</h1>
          <p class="lead">Abre un archivo Markdown y lo leo en voz alta en español de México, con pausas y entonación según su formato.</p>
          <button type="button" data-command="open">Abrir archivo…</button>
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
