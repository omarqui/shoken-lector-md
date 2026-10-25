# AGENTS.md — Shoken

App de Windows (WPF, .NET 10) que lee Markdown en voz alta en español de México. Usa el formato
del documento para decidir pausas y entonación, y resalta el párrafo que se está leyendo.
El `README.md` explica el uso y la configuración; este archivo es para quien modifica el código.

## Comandos

```powershell
dotnet build -c Release
dotnet test                                            # xUnit, solo Shoken.Core
dotnet run --project src/Shoken.App -c Release
dotnet publish src/Shoken.App -c Release -r win-x64 --self-contained false -o publicar
powershell -ExecutionPolicy Bypass -File empaquetado\empaquetar.ps1 [-Registrar]   # MSIX para la Store
```

Requiere el SDK de .NET 10 y el runtime de WebView2. Corre `dotnet test` antes de dar un cambio por terminado.

## Estructura

| Proyecto | Qué contiene |
|---|---|
| `src/Shoken.Core` (`net10.0`) | Toda la lógica. Sin dependencias de interfaz: no referenciar WPF aquí |
| `src/Shoken.App` (`net10.0-windows`) | WPF: WebView2 muestra el documento y `MediaPlayer` reproduce el audio. `Controls/` tiene la barra de título y el reproductor (`PlayerBar`) |
| `tests/Shoken.Core.Tests` | Pruebas xUnit del núcleo (`InternalsVisibleTo` habilitado) |
| `docs/ejemplo.md` | Documento con todos los elementos de Markdown: úsalo para probar a mano |
| `empaquetado/` | Manifiesto MSIX (`AppxManifest.xml`), `empaquetar.ps1` (arma el paquete para la Microsoft Store) , `generar-icono.cs` (genera `Assets/app.ico` desde `Assets/logo.svg`; no edites el `.ico` a mano) y `generar-demo.cs` (graba la demo de entonación) |

El flujo, de punta a punta:

1. `Parsing/MarkdownSpeechParser` (Markdig) convierte el Markdown en `SpeechSegment`s y en HTML con `data-seg` para el resaltado.
2. `Text/SpeechTextNormalizer` y `PronunciationDictionary` preparan el texto para la voz.
3. `Planning/ChunkPlanner` agrupa los segmentos en bloques de audio (~700 caracteres).
4. `Ssml/SsmlBuilder` arma el SSML con un `<bookmark>` por segmento, pausas y prosodia.
5. `Tts/HybridTtsEngine` decide el motor: caché → Azure (`AzureTtsEngine`) → Google (`GoogleTtsEngine`) → Kokoro (`KokoroTtsEngine`) → Piper (`PiperTtsEngine`). Los locales van en lista: si uno falla y hay otro después, descansa 2 minutos. El selector "Usar" del panel del estado puede dejar uno solo, sin respaldo.
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
  plano, en logs ni en pruebas. Su entropía conserva el nombre anterior de la app
  (`LectorMd.AzureKey.v1`): si la cambias, las claves ya guardadas no se pueden descifrar.
- Los datos del usuario viven en `%LOCALAPPDATA%\Shoken\`. No se versionan.
- **Empaquetada (MSIX, Store), la app guarda en el `LocalState` de su paquete** (`AppPaths.DefaultRoot`). Si guardara en
  `%LOCALAPPDATA%\Shoken`, Windows redirigiría la escritura a una copia privada que el Bloc de notas y el Explorador no
  ven. Toma las rutas de datos de la app siempre de `AppPaths`, no de `Environment.GetFolderPath`. El paquete es *full trust*: el
  hotkey, la bandeja, el pipe, DPAPI y lanzar procesos (Piper, VS Code) funcionan igual. La versión de
  `Directory.Build.props` lleva tres números, porque la Store reserva el cuarto (el script agrega `.0`).
- **`MediaPlayer` no arranca en 0 exacto:** si un bloque empieza en la posición 0 y luego se le pone una velocidad distinta de 1 (`SpeedRatio`), salta de 1 a 5 s hacia adelante y se pierde el inicio (los párrafos que abren su bloque y cada cambio de bloque). `PlayChunkAsync` arranca siempre en al menos 1 ms. Para medir el arranque: `LECTORMD_DIAGNOSTICO=1` escribe `diagnostico.log` en la carpeta de datos (un salto se ve como la posición real adelantándose de golpe).
- **"Abrir con":** sin empaquetar, `App` llama a `FileAssociation.Register` al arrancar y deja un ProgID `Shoken.Markdown` en `HKCU\Software\Classes` (más `OpenWithProgids` de `.md` y `.markdown`) apuntando al `Shoken.exe` que corre. Empaquetada no lo hace: la asociación está en `AppxManifest.xml` (y el HKCU de un paquete es virtual). Nunca intentes volverla predeterminada por código: Windows lo bloquea (hash de `UserChoice`).
- **Archivos arrastrados sobre la ventana:** el WebView2 tiene su propia ventana, así que WPF no ve lo que pasa sobre el documento ni puede dibujar encima. Por eso, al entrar un arrastre con archivos, `MainWindow` abre `DropOverlayWindow`: una ventana transparente, propiedad de la principal (no `Topmost`) y de su mismo tamaño, que pasa a recibir el arrastre. Como ve los nombres, dice antes de soltar qué se abrirá (los textos y la lista de extensiones están en `Core/Desktop/DraggedFiles`). La abre el `DragEnter` de WPF o, si el arrastre entra directo por el documento, el mensaje `dragFiles` de `lector.js` (que no se manda si lo arrastrado sale de la propia página, como una imagen). Su contenido no recibe el mouse, así que `DragLeave` solo llega al salir de la ventana o al cancelar; si el arrastre termina sin pasar por la capa, se cierra sola al ver que no queda ningún botón del mouse apretado. Si se suelta sobre el documento antes de que la capa reciba el arrastre, los archivos llegan por `lector.js` (`drop` con `postMessageWithAdditionalObjects`; `DocumentView` saca la ruta de cada `CoreWebView2File`), y el aviso de "no es Markdown" llega después.
- **Una sola instancia** (`Core/Ipc/SingleInstanceChannel`, mutex + named pipe): la segunda instancia le pasa su archivo a la primera y termina. Cerrar la ventana la oculta en la bandeja (`TrayIcon`); solo *Salir* o el cierre de sesión terminan la app. `Ctrl+Alt+L` es `GlobalHotkey` (RegisterHotKey) y el `MiniPlayerWindow` lo alimenta `MainWindow` con los mismos eventos que el `PlayerBar`.
- **WinForms solo para `NotifyIcon`:** el proyecto quita los `using` implícitos de `System.Windows.Forms` y `System.Drawing`; úsalos con alias (`Forms.`, `Drawing.`) para no chocar con WPF.
- **Popups y foco de teclado:** los `Popup` de WPF no se activan (`WS_EX_NOACTIVATE`); sus teclas les llegan por la ventana principal. Si el foco de Windows está en el WebView2 (el usuario acaba de hacer clic en el documento), `Focus()` sobre un control del popup no basta: las teclas siguen yendo a `lector.js`. Enfoca primero la ventana (`Focus()`) y luego el control, como `OnPronounceChipClick`.
- **La búsqueda (Ctrl+F) vive en la página** (`lector.js`, `CSS.highlights`), no en WPF: así no tapa el documento con otra ventana ni pelea por el foco. La app solo recibe los resultados por bloque (`find`) para el índice (`DocumentOutline.CountMatches`).
- **Diagramas Mermaid:** Markdig deja cada ` ```mermaid ` en `<pre class="mermaid" data-seg>` y `lector.js` lo dibuja ahí mismo con `Assets/mermaid.min.js` (12.1.0, MIT, de npm; se incluye para funcionar sin internet y con la CSP, que solo deja correr scripts de `app.lector`). Se carga solo si el documento tiene diagramas. Para actualizarlo: `npm pack mermaid@<versión>` y copia `package/dist/mermaid.min.js` y `package/LICENSE` (como `mermaid.LICENSE.txt`). Los colores salen de las variables de `lector.css`.
- **Tamaños de texto de la interfaz:** no escribas `FontSize="12"` en un texto. Usa `{DynamicResource LectorFontSize12}` (el número es el tamaño de diseño en Normal; `TextScale` le da el valor según Configuración) o `SetResourceReference` en código. Cada ventana toma `LectorFontSize14` como su FontSize y los controles Fluent lo heredan. Los íconos (`IconText`) sí van fijos.
- **Kokoro fonetiza como en España** y `KokoroPhonemes.ToMexican` lo pasa a México (θ → s, ʎ → ʝ) antes de sintetizar. Si cambias ese paso, sube `PhonemeVersion` en `KokoroTtsEngine`: es parte de la clave de caché. Las tres voces en español viven en `src/Shoken.App/Assets/kokoro` (se copian junto al exe); Core referencia `KokoroSharp.CPU` con `ExcludeAssets="buildTransitive;contentFiles"` para que no copie sus 61 voces (79 MB). El modelo no se versiona ni se distribuye: Configuración lo descarga (`KokoroModelDownloader`) a `%LOCALAPPDATA%\Shoken\kokoro`. Una pasada del modelo admite unos 500 tokens; las oraciones más largas se parten (`KokoroPhonemes.Split`).
- **Colores del diseño en un solo lugar:** `Core/Settings/ThemePalettes.cs` tiene los cuatro temas (Clásico, Papel, Grafito, Sepia y noche), cada uno en claro y oscuro. `ThemeService` los pone en los recursos `Lector*Brush` de la ventana y `DocumentView.SetColorsAsync` en las variables de `lector.css` (cuyos valores propios solo son el respaldo). Las pruebas de `ThemeTests` exigen contraste WCAG AA en cada tema: si cambias un color y fallan, ajústalo en vez de bajar el umbral.
- **Demo de entonación (`Core/Demo`, `MainWindow.Demo.cs`, `Assets/demo.js` y `demo.css`):** la pestaña «Así entona Shoken» lee `Core/Demo/asi-lee-shoken.md` con y sin entonación. La versión grabada (Azure, Dalia, diccionario y opciones por omisión) son `Assets/demo/demo-con.mp3` y `demo-sin.mp3`, con sus marcas y las huellas de su SSML en `Core/Demo/grabacion.json`. Las genera `dotnet run empaquetado/generar-demo.cs` (gasta unos 2 600 caracteres de Azure; `-- --seco` solo muestra el SSML). La prueba `Demo_RecordingMatchesCurrentSsml` falla si cambia el texto, el parser, el normalizador, el planificador o `SsmlBuilder` hasta que la vuelvas a grabar: no la arregles cambiando las huellas a mano. Las notas de cada parte salen de `IntonationDemo.Describe` (las mismas reglas de `ProsodyProfile.ForKind`/`ForRun` y `Pauses` que el SSML), no se escriben a mano. «Tu voz» pasa por la cadena normal (misma caché y cuota): antes de gastar nube pregunta (`HybridTtsEngine.EstimateCloudCost`, que usa `IBillableEngine`), y si los bloques salen de motores distintos no compara. La versión plana es `IntonationDemo.FlatChunk`: un solo párrafo sin estilos ni pausas, que sirve para cualquier motor. La CSP deja reproducir audio de `app.lector` solo por la demo. La pestaña (`DocumentTab.IsDemo`) no se guarda en la sesión ni se reutiliza al abrir o pegar, y en ella Espacio, Esc y las flechas manejan su reproductor (`demo.js` los atiende antes que `lector.js`; desde WPF, `ExecuteCommand` los manda a `window.shokenDemo`).
- **Apuntes (`Core/Notes`, `MainWindow.Notes.cs`, módulo `notes` de `lector.js`):** se anclan por su **cita**, no por el `data-seg` (que cambia al editar el Markdown). Al abrir, la página busca cada cita en el texto sin acentos (`textIndex`), en este orden (`locate`): tal cual a ±40 líneas de la guardada (`data-line` de cada bloque, que sale de `SpeechSegment.Line`/`EndLine`); si no, el tramo más parecido sobre las palabras plegadas de esos bloques tomadas seguidas, así que puede cruzar de un bloque al siguiente (`2·LCS/(|a|+|b|) ≥ 0.6`; si esos tramos pasan de `MAX_LCS_CELLS` o la cita de 400 palabras, se compara bloque por bloque), cuyo texto llega a la app como `current` (`DocumentNote.CurrentQuote`, no se guarda); y si no, tal cual en cualquier parte. Entre varias, la más cercana. La app es la dueña: guarda en `%LOCALAPPDATA%\Shoken\apuntes\<hash de la ruta>.json` (`NoteStore`), traduce anclas a líneas y sección (`NoteReferences`) y arma la exportación (`NoteExporter`, plantilla en `AppSettings.NotesInstruction`; null = la de la app). Los mensajes de apuntes solo se aceptan de la página actual (`IsCurrentPage`). Los apuntes no se leen ni tocan el SSML: no cambian la caché ni la cuota. Las tarjetas se ubican en `layout()`: cada una a la altura de su cita y, si se pisan, la de abajo se corre; la elegida (`pinnedId`: navegada con `Alt`+`↓`/`↑` o `notes.go`, tocada o en edición) queda exacta y corre hacia arriba a las anteriores. `drawLink()` une la cita marcada con su tarjeta.
- **Recarga automática:** `DocumentWatcher` vigila solo el archivo de la pestaña activa (las demás lo releen en `ActivateAsync` al activarse; los textos pegados no se vigilan). Avisa tras 400 ms sin eventos, y `MainWindow` compara el texto con el cargado. Qué hacer lo decide `ReloadPolicy.Decide` (Core, con pruebas): no repitas sus reglas en la App. Si hay un apunte abierto para escribir (`noteEditing` de `lector.js` → `DocumentView.IsEditingNote`), la recarga espera a que se guarde o se cancele, porque recargar navega la página y se perdería lo escrito; mientras, un aviso sin botones lo dice (una vez por espera). El aviso es el módulo `notice` de `lector.js`; sus botones llegan como comandos (`reloadNow`, `reloadOnPause`, `reloadDismiss`). Sin botones lleva la clase `quiet`: va por debajo de los apuntes (`z-index`) y no recibe el mouse, para no tapar ni quitarle el foco al que se escribe. La recarga que espera se cambia con la propiedad `PendingReload`, no con el campo: con «Al pausar», ella le pone al mini reproductor la indicación que dura (`SetPendingNotice`).
- **Lo escuchado (`Core/Coverage`, `MainWindow.Coverage.cs`, módulo `coverage` de `lector.js`):**
  - Un bloque cuenta como escuchado cuando sus segmentos suenan de principio a fin sin saltos. Lo decide `HeardTracker`, alimentado por `ReadAloudController` (`Started` al empezar, `Moved` al avanzar solo, `Jumped` al moverse con −10/+10 dentro del bloque de audio o al empezar a mitad de un párrafo, `Reset` al detener; tras un `Jumped`, los `Moved` que no pasan del párrafo al que se saltó no lo cuentan desde el inicio). `DocumentCoverage` junta los segmentos por bloque.
  - Se reconoce por `SpeechSegment.BlockKey`, que es el hash del Markdown fuente del bloque, no del texto para la voz: cambiar las opciones de lectura o el diccionario no lo borra, y editar un bloque sí.
  - Se guarda qué sonó (`HeardSpoken`: completo o solo el anuncio) y no la conclusión, para que el futuro diseño de cambios aplique su propia regla. Va en `escuchado\<hash de la ruta>.json` (`CoverageStore`), 2 s después de lo escuchado y enseguida al cambiar o cerrar la pestaña y al salir.
  - La línea del margen se ancla a su bloque (`anchor-name`, `position-anchor`) y `lector.js` le calcula `--heard-left` para que todas queden en una columna. No la ubiques dentro del bloque con `position: relative`: el `<pre>` y la tabla se desplazan a lo ancho y la recortarían.
- `ReadingTimeline` (Core) da la duración por segmento y capítulo: real para los bloques en caché (`AudioChunk.Duration`, guardada en la caché) y estimada (15 caracteres/s + pausas) para el resto.

## Estilo

- C# con `Nullable` e `ImplicitUsings` (ver `Directory.Build.props`). Clases `sealed` por defecto.
- Comentarios, `<summary>`, mensajes de la interfaz y nombres de pruebas descriptivos en **español**.
  Los identificadores de código van en inglés.
- Pruebas con nombre `Componente_Comportamiento` (por ejemplo `Ssml_IsValidXml_WithOneBookmarkPerSegment`).
- Si cambias cómo se lee un elemento de Markdown, actualiza la tabla "Cómo se lee cada elemento"
  del `README.md` y agrega el caso a `docs/ejemplo.md`.
