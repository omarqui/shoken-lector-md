# AGENTS.md — Lector MD

App de Windows (WPF, .NET 10) que lee Markdown en voz alta en español de México. Usa el formato
del documento para decidir pausas y entonación, y resalta el párrafo que se está leyendo.
El `README.md` explica el uso y la configuración; este archivo es para quien modifica el código.

## Comandos

```powershell
dotnet build -c Release
dotnet test                                            # xUnit, solo LectorMd.Core
dotnet run --project src/LectorMd.App -c Release
dotnet publish src/LectorMd.App -c Release -r win-x64 --self-contained false -o publicar
```

Requiere el SDK de .NET 10 y el runtime de WebView2. Corre `dotnet test` antes de dar un cambio por terminado.

## Estructura

| Proyecto | Qué contiene |
|---|---|
| `src/LectorMd.Core` (`net10.0`) | Toda la lógica. Sin dependencias de interfaz: no referenciar WPF aquí |
| `src/LectorMd.App` (`net10.0-windows`) | WPF: WebView2 muestra el documento y `MediaPlayer` reproduce el audio. `Controls/` tiene la barra de título y el reproductor (`PlayerBar`) |
| `tests/LectorMd.Core.Tests` | Pruebas xUnit del núcleo (`InternalsVisibleTo` habilitado) |
| `docs/ejemplo.md` | Documento con todos los elementos de Markdown: úsalo para probar a mano |

El flujo, de punta a punta:

1. `Parsing/MarkdownSpeechParser` (Markdig) convierte el Markdown en `SpeechSegment`s y en HTML con `data-seg` para el resaltado.
2. `Text/SpeechTextNormalizer` y `PronunciationDictionary` preparan el texto para la voz.
3. `Planning/ChunkPlanner` agrupa los segmentos en bloques de audio (~700 caracteres).
4. `Ssml/SsmlBuilder` arma el SSML con un `<bookmark>` por segmento, pausas y prosodia.
5. `Tts/HybridTtsEngine` decide el motor: caché → Azure (`AzureTtsEngine`) → Google (`GoogleTtsEngine`) → Piper (`PiperTtsEngine`). El selector "Usar" del panel del estado puede dejar uno solo, sin respaldo.
6. `Caching/AudioCache` guarda el audio; `Usage/UsageTracker` lleva la cuota mensual de Azure.

`EngineServices.Create` arma todas las piezas a partir de `AppSettings`.

## Reglas que no se ven a simple vista

- **El plan de bloques es determinista.** `ChunkPlanner` no puede depender de dónde empieza a leer
  el usuario ni de nada que no sea el documento y las opciones. Si el plan cambia, la clave de
  caché cambia y se vuelve a gastar cuota de Azure.
- **La clave de caché es el SHA-256 del SSML** (Azure) o de sus equivalentes (Piper), vía
  `AudioCache.ComputeKey`. Cualquier cambio en `SsmlBuilder` o en el normalizador invalida el audio
  ya generado. Está bien si es intencional; no lo hagas como efecto secundario de un refactor.
- **Azure cobra el SSML, no solo el texto** (todo salvo `<speak>` y `<voice>`). Cada pausa,
  bookmark o `<prosody>` cuesta caracteres de la capa F0 (500 000/mes). Por eso la cursiva y las
  citas se distinguen con pausas y no con prosodia. Antes de agregar etiquetas, piensa en el costo.
- **Los `<bookmark>` van como hijos directos de `<voice>`** y hay exactamente uno por segmento,
  numerados desde 0. El resaltado depende de eso; hay pruebas que lo verifican.
- **Límite de peticiones de F0: 20 por minuto.** La app adelanta solo 2 bloques. No subas el
  adelanto ni bajes el tamaño de bloque sin revisar esto.
- La clave de Azure se guarda cifrada con DPAPI (`SettingsStore`). Nunca la escribas en texto
  plano, en logs ni en pruebas.
- Los datos del usuario viven en `%LOCALAPPDATA%\LectorMd\`. No se versionan.
- **Una sola instancia** (`Core/Ipc/SingleInstanceChannel`, mutex + named pipe): la segunda instancia le pasa su archivo a la primera y termina. Cerrar la ventana la oculta en la bandeja (`TrayIcon`); solo *Salir* o el cierre de sesión terminan la app. `Ctrl+Alt+L` es `GlobalHotkey` (RegisterHotKey) y el `MiniPlayerWindow` lo alimenta `MainWindow` con los mismos eventos que el `PlayerBar`.
- **WinForms solo para `NotifyIcon`:** el proyecto quita los `using` implícitos de `System.Windows.Forms` y `System.Drawing`; úsalos con alias (`Forms.`, `Drawing.`) para no chocar con WPF.
- **Popups y foco de teclado:** los `Popup` de WPF no se activan (`WS_EX_NOACTIVATE`); sus teclas les llegan por la ventana principal. Si el foco de Windows está en el WebView2 (el usuario acaba de hacer clic en el documento), `Focus()` sobre un control del popup no basta: las teclas siguen yendo a `lector.js`. Enfoca primero la ventana (`Focus()`) y luego el control, como `OnPronounceChipClick`.
- **La búsqueda (Ctrl+F) vive en la página** (`lector.js`, `CSS.highlights`), no en WPF: así no tapa el documento con otra ventana ni pelea por el foco. La app solo recibe los resultados por bloque (`find`) para el índice (`DocumentOutline.CountMatches`).
- **Diagramas Mermaid:** Markdig deja cada ` ```mermaid ` en `<pre class="mermaid" data-seg>` y `lector.js` lo dibuja ahí mismo con `Assets/mermaid.min.js` (12.1.0, MIT, de npm; se incluye para funcionar sin internet y con la CSP, que solo deja correr scripts de `app.lector`). Se carga solo si el documento tiene diagramas. Para actualizarlo: `npm pack mermaid@<versión>` y copia `package/dist/mermaid.min.js` y `package/LICENSE` (como `mermaid.LICENSE.txt`). Los colores salen de las variables de `lector.css`.
- **Colores del diseño en dos lugares:** `ThemeService.ApplyPalette` (recursos `Lector*Brush` de la ventana) y las variables de `Assets/lector.css` (el documento). Si cambias uno, cambia el otro.
- `ReadingTimeline` (Core) da la duración por segmento y capítulo: real para los bloques en caché (`AudioChunk.Duration`, guardada en la caché) y estimada (15 caracteres/s + pausas) para el resto.

## Estilo

- C# con `Nullable` e `ImplicitUsings` (ver `Directory.Build.props`). Clases `sealed` por defecto.
- Comentarios, `<summary>`, mensajes de la interfaz y nombres de pruebas descriptivos en **español**.
  Los identificadores de código van en inglés.
- Pruebas con nombre `Componente_Comportamiento` (por ejemplo `Ssml_IsValidXml_WithOneBookmarkPerSegment`).
- Si cambias cómo se lee un elemento de Markdown, actualiza la tabla "Cómo se lee cada elemento"
  del `README.md` y agrega el caso a `docs/ejemplo.md`.
