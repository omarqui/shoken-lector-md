# Recarga automática, apuntes que siguen al texto, marcar sin parar y cobertura — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Que la vuelta de revisión con la IA no tenga fricción: el documento se recarga solo, los apuntes siguen al texto retocado, `M` marca sin parar y se ve qué partes ya se escucharon.

**Architecture:** La lógica nueva va en `Shoken.Core` con pruebas: la clave de bloque en el parser, `ResumeLocator` y `ReloadPolicy`, el exportador de apuntes y `Core/Coverage` (`HeardTracker`, `DocumentCoverage`, `CoverageStore`). La app WPF conecta: `DocumentWatcher` vigila el archivo, `ReadAloudController` alimenta el `HeardTracker` y `MainWindow` pinta índice y línea de tiempo. La página (`lector.js`) hace el anclaje por parecido, el aviso de recarga y la línea de cobertura. No se toca nada que cambie el audio.

**Tech Stack:** .NET 10, WPF, WebView2, xUnit, JavaScript sin dependencias.

**Spec:** `docs/superpowers/specs/2026-10-24-recarga-apuntes-marcas-cobertura-design.md` (léelo antes de cada tarea).

## Global Constraints

- **No se toca el audio:** ni `SsmlBuilder`, ni el normalizador, ni `ChunkPlanner`. Las claves de caché y el plan de bloques quedan iguales. Ninguna de estas mejoras puede gastar cuota de Azure por sí misma.
- **Se compara el texto del documento, no el que se dice:** nunca `SpeechSegment.PlainText` para reconocer un bloque entre versiones. Se usa `SpeechSegment.BlockKey` (Tarea 1).
- **La unidad visible es el bloque de la página:** lo que lleva `data-seg`. Los segmentos de un bloque comparten `AnchorId`.
- **Las rutas de datos salen de `AppPaths`**, nunca de `Environment.GetFolderPath`.
- **Solo archivos `.md`:** los textos pegados no se vigilan ni guardan cobertura.
- **Estilo:**
  - Lógica en `Shoken.Core` con pruebas xUnit con nombre `Componente_Comportamiento`.
  - Comentarios, `<summary>`, textos de la interfaz y nombres de pruebas en español; identificadores en inglés.
  - Clases `sealed`. Tamaños de texto con `{DynamicResource LectorFontSize…}`. Colores en `ThemePalettes`.
  - Escribe como el código que rodea tu cambio: la misma densidad de comentarios, nombres e idioms.
- **Documentación:** actualiza el `README.md` (tabla de Uso, sección de apuntes y «Datos que guarda») y `AGENTS.md` donde la tarea lo pida.
- **Pruebas:** `dotnet test` en verde al terminar cada tarea.
- **Nunca simules mouse ni teclado** (`SendInput`, `SetCursorPos`, UI Automation que mueva el foco): el usuario está escribiendo en otras ventanas del mismo escritorio.
- **Para probar la página,** cierra Shoken con cuidado (`(Get-Process Shoken).CloseMainWindow()`, y si no termina, `Stop-Process`). Luego lánzalo con `$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=9333'`, solo para ese proceso, y maneja la página con CDP desde node 24 (WebSocket global): `Runtime.evaluate`, `Page.captureScreenshot`. Al terminar, reinicia Shoken **sin** el puerto.
- Si `dotnet build` de la app falla porque `Shoken.exe` está abierto y bloquea los archivos, ciérralo como se indica arriba y vuelve a intentar. No borres nada de `%LOCALAPPDATA%\Shoken`.
- **Sin commits (decisión del usuario):** se trabaja en `main`, en la misma carpeta donde otras sesiones dejaron cambios
  sin commit (la demo, `DropOverlayWindow`, etc.). **No hagas `git add`, `git commit`, `git stash`, `git checkout` ni
  `git restore`**, y no reviertas ni reformatees nada que no sea tuyo: hay archivos con cambios ajenos (`README.md`,
  `AGENTS.md`, `MainWindow.xaml.cs`, `lector.js`, `DocumentView.cs`…). Edita con reemplazos exactos sobre lo que hay.
  Los pasos «Commit» del plan se reemplazan por: deja los cambios en el árbol y reporta la lista de archivos tocados.

## Review Focus

- **Un editor que guarda en varias pasadas** (VS Code, la herramienta Write de Claude: temporal + renombrar) produce **una** recarga, y un guardado sin cambios no recarga. Lo prueban la Tarea 2 (`ReloadPolicy` con contenido igual) y la verificación de la Tarea 4.
- **El archivo cambia mientras se escribe un apunte:** no se pierde lo escrito. Lo prueban la Tarea 2 (`Reload_WaitsForTheNote_WhileANoteIsBeingWritten`) y la verificación de la Tarea 4.
- **Una cita corta retocada que aparece tal cual en otra sección:** el apunte se queda cerca y no salta allá. Lo prueba la verificación CDP de la Tarea 6.
- **Saltar con ±10 s sobre un párrafo** no lo cuenta como escuchado; pausar y seguir dentro de un párrafo sí lo cuenta. Lo prueba la Tarea 8 (`HeardTracker_*`).
- **Claude inserta texto antes del párrafo donde se pausó:** al recargar se retoma en ese mismo párrafo, no en el número viejo. Lo prueban la Tarea 2 (`Resume_FindsTheSameParagraph_AfterTextWasInsertedBefore`) y la verificación de la Tarea 3.

---

### Task 1: Clave de bloque y anuncios en el parser

**Files:**
- Modify: `src/Shoken.Core/Model/SpeechModel.cs` (propiedades nuevas de `SpeechSegment`)
- Modify: `src/Shoken.Core/Parsing/MarkdownSpeechParser.cs` (`AddSegments`, `State`, los llamados que anuncian: fórmula ~línea 133, diagrama ~226, bloque de código ~233, tabla en modo Announce ~270)
- Test: `tests/Shoken.Core.Tests/MarkdownSpeechParserTests.cs`

**Interfaces:**
- Produces:
  - `SpeechSegment.BlockKey`: `public string BlockKey { get; init; } = "";` Son 16 caracteres hex en minúscula del SHA-256 (UTF-8) del Markdown fuente del bloque (`markdown.Substring(block.Span.Start, block.Span.Length)`), con los espacios en blanco seguidos colapsados a uno y recortado. Todos los segmentos de un bloque llevan la misma clave.
  - `SpeechSegment.IsAnnouncement`: `public bool IsAnnouncement { get; init; }`. Es true cuando el texto es un anuncio en lugar del contenido: «Fórmula.», «Diagrama.», «Bloque de código…» y la tabla en modo Announce.
  - `State.KeyOf(Block block)`: `string`, privado del parser.
  - `AddSegments(..., bool announcement = false)`.

Límite aceptado: una tabla en modo Announce es un solo bloque (toda la tabla) y en ReadRows es una fila por bloque. Cambiar ese modo cambia las claves de sus filas.

- [ ] **Step 1: Escribe las pruebas que fallan**
  - `Parser_BlockKey_IsTheSameWhateverTheReadingOptions`: parsea ``"```cs\nvar x = 1;\n```\n\nTexto ![logo](a.png) final."`` cuatro veces: con `CodeBlocks = CodeBlockMode.Announce` y `CodeBlockMode.Read`, y con `ReadImageAltText = false` y `true`.
    - La `BlockKey` del segmento del bloque de código es igual en las cuatro.
    - La `BlockKey` del párrafo es igual en las cuatro.
    - Las claves miden 16 caracteres.
  - `Parser_BlockKey_ChangesOnlyForTheEditedBlock`: `"Uno.\n\nDos."` frente a `"Uno.\n\nDos y tres."`. La clave del primero es igual; la del segundo, distinta.
  - `Parser_BlockKey_IsSharedByThePartsOfALongParagraph`: con `new MarkdownSpeechOptions { LongSegmentChars = 80 }`, un párrafo de unas 5 oraciones de 40 caracteres. Hay más de un segmento, y todos tienen el mismo `AnchorId` y la misma `BlockKey`.
  - `Parser_IsAnnouncement_OnlyForAnnouncedBlocks`: un bloque de código en Announce da `true` y en Read da `false`; un ` ```mermaid ` da `true`; un párrafo da `false`.

- [ ] **Step 2: Corre las pruebas y verifica que fallan**

  Run: `dotnet test --filter "FullyQualifiedName~MarkdownSpeechParserTests"`
  Expected: FAIL (no existen `BlockKey` ni `IsAnnouncement`).

- [ ] **Step 3: Implementa.** `State` recibe ya el `markdown`; agrega `KeyOf`. `AddSegments` asigna `BlockKey = state.KeyOf(block)` e `IsAnnouncement = announcement`. Los cuatro llamados que anuncian pasan `announcement: true`.

- [ ] **Step 4: Corre todas las pruebas**

  Run: `dotnet test`
  Expected: PASS, incluidas las de SSML y plan de bloques sin cambios.

- [ ] **Step 5: Commit** — «Agrega la clave de bloque y la marca de anuncio a los segmentos»

---

### Task 2: Retomar por contenido y política de recarga (Core)

**Files:**
- Create: `src/Shoken.Core/Model/ResumeLocator.cs`
- Create: `src/Shoken.Core/Model/ReloadPolicy.cs`
- Modify: `src/Shoken.Core/Settings/OpenTab.cs`
- Test: `tests/Shoken.Core.Tests/ResumeLocatorTests.cs`, `tests/Shoken.Core.Tests/ReloadPolicyTests.cs`

**Interfaces:**
- Consumes: `SpeechSegment.BlockKey` (Tarea 1).
- Produces:
  - `public sealed record ResumePoint(int SegmentId, string? BlockKey, int Part)` con `public static ResumePoint Of(SpeechDocument document, int segmentId)`. `Part` es el índice del segmento dentro de su bloque (0 para el primero). Con un id fuera de rango devuelve `new(segmentId, null, 0)`.
  - `public static class ResumeLocator` con `public static int Find(SpeechDocument document, ResumePoint point)`:
    - Devuelve -1 si el documento no tiene segmentos.
    - Si `BlockKey` es null, devuelve el id acotado a `[0, Count-1]`.
    - Si no, busca el bloque con esa clave cuyo primer segmento esté más cerca de `point.SegmentId` y devuelve su primer segmento + `min(Part, partes-1)`.
    - Si no hay ninguno, devuelve el id acotado («el que ocupa su lugar»).
  - `public enum ReloadAction { Ignore, ReloadNow, Ask, WhenPaused, WhenEditingEnds }`
  - `public static class ReloadPolicy` con `public static ReloadAction Decide(bool contentChanged, bool playing, bool windowInFront, bool editingNote)`, en este orden:
    1. `!contentChanged` → `Ignore`.
    2. `editingNote` → `WhenEditingEnds`.
    3. `!playing` → `ReloadNow`.
    4. `windowInFront` → `Ask`.
    5. Si no → `WhenPaused`.
  - `OpenTab`: `public string? ResumeBlockKey { get; set; }` y `public int ResumePart { get; set; }`, para guardar en `settings.json` junto a `ResumeSegmentId`.

- [ ] **Step 1: Escribe las pruebas que fallan**
  - `Resume_FindsTheSameParagraph_AfterTextWasInsertedBefore`: v1 `"# A\n\nUno.\n\nDos.\n\nTres."` con el punto en «Tres.» (id 3). v2 inserta `"Nuevo.\n\n"` después del título. `Find` da el id de «Tres.» en v2, que es 4.
  - `Resume_KeepsThePartWithinALongParagraph`: con `LongSegmentChars = 80`, un párrafo partido en 3 o más y el punto en su parte 2. Tras insertar un párrafo antes, da el primer segmento del bloque + 2.
  - `Resume_FallsBackToTheSameIndex_WhenTheParagraphChanged`: si en v2 «Tres.» pasa a «Tres y medio.», da 3.
  - `Resume_PicksTheNearestCopy_WhenTheParagraphIsRepeated`: un documento con «Igual.» en los ids 1 y 5 y el punto en 5 (clave de «Igual.»). Da 5.
  - `Resume_WithoutKey_UsesTheIndex`: `new ResumePoint(99, null, 0)` sobre un documento de 4 segmentos da 3.
  - `Reload_IgnoresSaves_ThatDoNotChangeTheText`: `Decide(false, true, true, true)` da `Ignore`.
  - `Reload_IsImmediate_WhenNothingIsPlaying`: `Decide(true, false, false, false)` da `ReloadNow`.
  - `Reload_Asks_WhenPlayingWithTheWindowInFront`: `Decide(true, true, true, false)` da `Ask`.
  - `Reload_WaitsForThePause_WhenPlayingWithTheWindowHidden`: `Decide(true, true, false, false)` da `WhenPaused`.
  - `Reload_WaitsForTheNote_WhileANoteIsBeingWritten`: `Decide(true, false, true, true)` y `Decide(true, true, false, true)` dan `WhenEditingEnds`.

- [ ] **Step 2: Corre y verifica que fallan.** Run: `dotnet test --filter "FullyQualifiedName~ResumeLocatorTests|FullyQualifiedName~ReloadPolicyTests"`. Expected: FAIL de compilación.
- [ ] **Step 3: Implementa** `ResumePoint`, `ResumeLocator`, `ReloadPolicy` y los campos de `OpenTab`.
- [ ] **Step 4: Corre todas las pruebas.** Run: `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** — «Agrega el retomar por contenido y la política de recarga»

---

### Task 3: Retomar el mismo párrafo y conservar la vista al recargar

**Files:**
- Modify: `src/Shoken.App/MainWindow.xaml.cs` (`RememberPosition`, `ActivateAsync`, `ReloadAsync`)
- Modify: `src/Shoken.App/Services/DocumentView.cs`
- Modify: `src/Shoken.App/Assets/lector.js`

**Interfaces:**
- Consumes: `ResumePoint`, `ResumeLocator`, `OpenTab.ResumeBlockKey`/`ResumePart` (Tarea 2).
- Produces:
  - `DocumentView.GetVisibleBlockAsync()`: `Task<(string Anchor, double Offset)?>`, sobre `window.lector.visibleBlock()`. Devuelve el `data-seg` del primer bloque visible y cuántos píxeles de él quedaron arriba del borde, o `null`.
  - `DocumentView.ScrollToBlockAsync(string anchor, double offset)`: `Task`, sobre `window.lector.scrollToBlock(anchor, offset)`.
  - `MainWindow`: `private async Task ReloadCurrentAsync(bool rebuildServices, bool keepPlaying)`. F5 llama con `(true, false)` y la Tarea 4 con `(false, …)`.

Comportamiento:

1. `RememberPosition` guarda `ResumePoint.Of(_document, id)` en los tres campos de `OpenTab`.
2. En `ActivateAsync`, el párrafo de retomar se resuelve siempre con `ResumeLocator.Find(documentoNuevo, punto)`:
   - El punto sale de `ResumePoint.Of(_document, resumeSegmentId)` si llega `resumeSegmentId`, la pestaña es la activa y `_document` es el documento anterior. Así funcionan sin cambios los cinco llamados que ya pasan `resumeSegmentId`.
   - Si no, sale de los campos de `tab.State`.
3. `ReloadCurrentAsync`:
   1. Toma el bloque visible y lo traduce al documento nuevo: ancla vieja → primer segmento de esa ancla → `ResumePoint` → `Find` → ancla nueva.
   2. Recarga.
   3. Restaura la vista con `ScrollToBlockAsync` en lugar de `ScrollY`.
   4. Si `keepPlaying` y estaba sonando, vuelve a `PlayFrom` el párrafo resuelto.

- [ ] **Step 1: Implementa** lo de `lector.js` (`visibleBlock`, `scrollToBlock`, expuestos en `window.lector`), `DocumentView` y `MainWindow`. F5 (`ReloadAsync`) pasa a usar `ReloadCurrentAsync(rebuildServices: true, keepPlaying: false)`.
- [ ] **Step 2: Compila y corre las pruebas.** Run: `dotnet build -c Release` y `dotnet test`. Expected: sin errores ni advertencias nuevas; PASS.
- [ ] **Step 3: Verifica en la app real, sin mouse:**
  1. Copia `docs/ejemplo.md` a una carpeta temporal y ábrela en Shoken con el puerto 9333 (`Shoken.exe <copia>`).
  2. Por CDP, haz scroll a la mitad (`window.scrollTo`) y empieza a leer desde un párrafo de la mitad. Puedes provocar el doble clic con `Runtime.evaluate` despachando `dblclick` sobre su `[data-seg]`. Luego pausa con `window.chrome.webview.postMessage({type:'command',command:'toggle'})`.
  3. Desde PowerShell, inserta dos párrafos al principio de la copia.
  4. Manda `command:'reload'`.
  5. Comprueba con `Runtime.evaluate` que el resaltado (`.lector-reading`) está en el mismo texto que antes y que el primer bloque visible es el mismo. Toma una captura.
- [ ] **Step 4: Commit** — «Retoma el mismo párrafo y conserva la vista al recargar»

---

### Task 4: Recarga automática

**Files:**
- Create: `src/Shoken.App/Services/DocumentWatcher.cs`
- Modify: `src/Shoken.App/MainWindow.xaml.cs` (activación de la pestaña, `ExecuteCommand`, `OnPlaybackStateChanged`)
- Modify: `src/Shoken.App/MainWindow.Notes.cs` (`NoteRequest.Editing`)
- Modify: `src/Shoken.App/Services/DocumentView.cs` (`NoteRequest.Editing`, aviso)
- Modify: `src/Shoken.App/Assets/lector.js` y `lector.css` (módulo `notice`, avisos de edición de apuntes)
- Modify: `README.md`, `AGENTS.md`

**Interfaces:**
- Consumes: `ReloadPolicy.Decide` (Tarea 2) y `ReloadCurrentAsync` (Tarea 3).
- Produces:
  - `internal sealed class DocumentWatcher(Dispatcher dispatcher) : IDisposable`:
    - `public void Watch(string? path)`: null deja de vigilar.
    - `public event Action<string>? Changed`: la ruta, tras 400 ms sin más eventos.
    - `public event Action<string>? Missing`: el archivo ya no está tras esa espera.
    - Usa un `FileSystemWatcher` sobre la carpeta con `Filter` = nombre del archivo y escucha Changed, Created, Renamed (desde y hacia ese nombre) y Deleted.
  - `NoteRequest.Editing(bool Editing)`: mensaje `{type:'noteEditing', editing}` que la página manda al abrir y al cerrar el editor de un apunte (en `startEditing`, `set` con `editId`, `commit`, `cancel` y `remove` del que se editaba). `DocumentView` expone `public bool IsEditingNote { get; }`.
  - `window.lector.notice.show(text, actions)` y `window.lector.notice.hide()`. `actions` es `[{ id, label }]`. Un clic manda `{type:'command', command: id}`. Usa el estilo del aviso de «Deshacer» de los apuntes (`.lector-undo`).
  - Comandos en `ExecuteCommand`: `reloadNow`, `reloadOnPause` y `reloadDismiss`.

Comportamiento, según el spec, sección 1:

- `MainWindow` guarda el texto Markdown cargado de la pestaña activa y vigila solo esa pestaña, y solo si no es pegada.
- **En `Changed`:** lee el archivo y llama a `ReloadPolicy.Decide` con estos datos:
  - `contentChanged`: el texto difiere del cargado.
  - `playing`: `_player.State` es `Playing` o `Loading`.
  - `windowInFront`: `IsActive && IsVisible && WindowState != Minimized`.
  - `editingNote`: `_view.IsEditingNote`.
- **Qué hace con cada resultado:**
  - `ReloadNow`: `ReloadCurrentAsync(false, false)` y el aviso breve «Documento actualizado» (`ShowToast`).
  - `Ask`: el aviso «El archivo cambió en el disco.» con «Recargar ahora», «Al pausar» y «Ahora no».
  - `WhenPaused`: queda pendiente para la próxima pausa o detención, se muestra el mismo aviso (para cuando vuelva el usuario) y `_mini?.ShowNotice("Se actualiza al pausar")`.
  - `WhenEditingEnds`: queda pendiente y se vuelve a decidir con `NoteRequest.Editing(false)`.
- **Botones del aviso:**
  - `reloadNow`: `ReloadCurrentAsync(false, keepPlaying: true)`.
  - `reloadOnPause`: lo deja pendiente para la pausa.
  - `reloadDismiss`: cierra el aviso. Un cambio nuevo lo vuelve a mostrar.
- **En `Missing`:** «El archivo ya no está en su carpeta.» (`ShowToast` con `ok: false`). La pestaña sigue abierta.
- Lo pendiente se descarta al cambiar de pestaña o al recargar por cualquier vía.

- [ ] **Step 1: Implementa** `DocumentWatcher`, el módulo `notice`, los avisos de edición y la conexión en `MainWindow`.
- [ ] **Step 2: Compila y corre las pruebas.** Run: `dotnet build -c Release` y `dotnet test`. Expected: sin errores; PASS.
- [ ] **Step 3: Verifica en la app real** (puerto 9333, una copia de `docs/ejemplo.md`, cambios escritos desde PowerShell):
  - **Detenido:** agregar una línea recarga sola una vez. Comprueba con `Runtime.evaluate` que el texto nuevo está en la página.
  - **Dos escrituras seguidas en menos de 400 ms** (escribe un temporal y renómbralo encima, como un editor) dan **una** recarga. Cuéntalo con un contador en la página o en el log.
  - **Guardar el mismo contenido** no recarga.
  - **Sonando con la ventana al frente:** aparece el aviso (`.lector-notice` visible). `reloadNow` por CDP recarga y sigue sonando desde el mismo párrafo.
  - **Con un apunte en edición** (abre el editor por CDP), el cambio no recarga hasta guardar la nota, y lo escrito queda guardado.
  - Toma capturas del aviso.
- [ ] **Step 4: Documenta.**
  - `README.md`: en Uso, cómo funciona la recarga automática (sin sonido, sonando y con la ventana oculta).
  - `AGENTS.md`: una línea en «Reglas que no se ven a simple vista». Dice que `DocumentWatcher` vigila solo la pestaña activa (las demás releen el archivo al activarse), que `ReloadPolicy` decide y que la recarga espera si hay un apunte en edición porque recargar navega la página.
- [ ] **Step 5: Commit** — «Recarga el documento cuando cambia en el disco»

---

### Task 5: Apuntes con cita cambiada en Core

**Files:**
- Modify: `src/Shoken.Core/Notes/DocumentNote.cs`
- Modify: `src/Shoken.Core/Notes/NoteExporter.cs`
- Test: `tests/Shoken.Core.Tests/NotesTests.cs`

**Interfaces:**
- Produces:
  - `DocumentNote.CurrentQuote`: `[JsonIgnore] public string? CurrentQuote { get; set; }`. Es el texto que hay hoy donde estaba la cita, cuando se encontró por parecido; null si está igual o huérfana. No se guarda y se recalcula al abrir.
  - En `NoteExporter.ToMarkdown`, un apunte con `CurrentQuote` exporta, después de su encabezado, este bloque y luego la nota:
    ```
    Cita original:

    > …cita…

    Ahora dice:

    > …texto actual…
    ```
  - `NoteExporter.DefaultInstruction`: agrega al final este párrafo (separado por una línea en blanco), literal:
    `Si un apunte trae «Cita original» y «Ahora dice», ese texto ya cambió desde que lo señalé: dime si el cambio resuelve mi nota y, si no, qué falta.`

- [ ] **Step 1: Escribe las pruebas que fallan**
  - `Exporter_ShowsTheOriginalAndTheCurrentText_WhenTheQuoteChanged`: una nota con `Quote = "Si la DGII rechaza el envío, reintenta tres veces."` y `CurrentQuote = "Si la DGII rechaza el envío, reintenta cinco veces."`. El resultado contiene `"Cita original:\n\n> Si la DGII rechaza el envío, reintenta tres veces.\n\nAhora dice:\n\n> Si la DGII rechaza el envío, reintenta cinco veces.\n\n"` seguido del texto de la nota.
  - `Exporter_QuotesOnlyTheOriginal_WhenTheQuoteDidNotChange`: sin `CurrentQuote`, el resultado no contiene «Ahora dice».
  - `Notes_CurrentQuote_IsNotSaved`: guarda con `NoteStore` una nota con `CurrentQuote`, la carga y verifica que `CurrentQuote` es null.
  - `Exporter_DefaultInstruction_ExplainsChangedQuotes`: `DefaultInstruction` contiene «Cita original» y «Ahora dice».
- [ ] **Step 2: Corre y verifica que fallan.** Run: `dotnet test --filter "FullyQualifiedName~NotesTests"`. Expected: FAIL.
- [ ] **Step 3: Implementa.**
- [ ] **Step 4: Corre todas las pruebas.** Run: `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** — «Exporta la cita original y la actual de los apuntes cuyo texto cambió»

---

### Task 6: Anclaje por parecido, tarjeta «La cita cambió» y huérfanos en bloque

**Files:**
- Modify: `src/Shoken.App/Assets/lector.js` (`textIndex`, módulo `notes`: `locate`, `anchorAll`, `cardHtml`, `ensureUi`) y `lector.css`
- Modify: `src/Shoken.App/Services/DocumentView.cs` (`NoteAnchor`)
- Modify: `src/Shoken.App/MainWindow.Notes.cs` (`UpdateLocations`)
- Modify: `README.md` (sección «Apuntes para una IA»)

**Interfaces:**
- Consumes: `DocumentNote.CurrentQuote` (Tarea 5).
- Produces:
  - `NoteAnchor(string Id, bool Found, string? StartAnchor, string? EndAnchor, string? Current)`. `Current` es el texto actual cuando se encontró por parecido; viene de `results[i].current` de `notesAnchored`.
  - `UpdateLocations` asigna `note.CurrentQuote = result.Current`.
  - `textIndex`:
    - `blocks()`: `[{ seg, line, start, end }]`, en offsets del texto plegado.
    - `rangeOf(start, end)`: `Range`.

Algoritmo de `locate(note)`, en este orden (spec, sección 2):

1. **Exacta cerca:** apariciones de la cita plegada con `|line − note.line| ≤ 40`. Gana la más cercana.
2. **Parecida cerca:** dentro de los bloques con `|line − note.line| ≤ 40`.
   - Las palabras se sacan del texto plegado (`split(/[^\p{L}\p{N}]+/u)` sin vacías).
   - Parecido = `2·LCS(palabras) / (|a| + |b|)`.
   - Candidatos: el bloque entero y, si la cita tiene menos palabras que el bloque, cada tramo contiguo de entre `ceil(0.7·n)` y `ceil(1.3·n)` palabras del bloque (n = palabras de la cita). Si la cita tiene más de 400 palabras, solo bloques enteros.
   - Gana el mayor parecido **≥ 0.6**; en un empate, la línea más cercana.
   - Devuelve `current` con `range.toString()` normalizado a un espacio.
3. **Exacta en cualquier parte:** como hoy, la más cercana.
4. Si no hay nada, huérfana.

`locate` devuelve `{ range, seg, endSeg, line, current }`, con `current` solo en el paso 2. `anchorAll` lo manda en `notesAnchored`.

Tarjeta:

- Una nota con `current` lleva la clase `changed` y la etiqueta «La cita cambió».
- Muestra la cita original atenuada y tachada (`.note-quote-old`) y el texto actual (`.note-quote-new`).
- Compacta, muestra una línea del texto actual.
- ▶ y «Resuelto» funcionan como siempre.

Huérfanos en bloque:

- Si hay pendientes huérfanos, la cabecera de la columna muestra «N apuntes ya no encuentran su cita · Marcar resueltos».
- Al pulsarlo, cada uno se marca resuelto (`noteUpdate` con `resolved: true`).
- Aparece el aviso «N apuntes marcados como resueltos · Deshacer»; deshacer manda `resolved: false` para esos mismos ids.

- [ ] **Step 1: Implementa** lo de la página, `DocumentView` y `MainWindow.Notes.cs`.
- [ ] **Step 2: Compila y corre las pruebas.** Run: `dotnet build -c Release` y `dotnet test`. Expected: sin errores; PASS.
- [ ] **Step 3: Verifica con CDP** (puerto 9333) sobre una copia de `docs/ejemplo.md`. Crea apuntes por CDP: selecciona un rango y llama a `window.lector.notes.createFromSelection()`, luego guarda la nota. Después edita el archivo desde PowerShell y recarga:
  - **Retocar 1 o 2 palabras de una cita de párrafo entero:** la tarjeta tiene `changed`, muestra las dos versiones y el `notesAnchored` trae `current`.
  - **Una cita corta (3 palabras) retocada, con esas mismas 3 palabras tal cual en otra sección lejana:** el apunte queda en el párrafo retocado, cerca (paso 2), y no salta lejos.
  - **Un párrafo movido intacto lejos:** se encuentra en su nuevo lugar (paso 3).
  - **Un párrafo reescrito por completo:** huérfano.
  - **«Marcar resueltos» y «Deshacer»:** funcionan.
  - **«Copiar para la IA»:** lee el portapapeles desde PowerShell (`Get-Clipboard`) y comprueba que trae «Cita original» y «Ahora dice».
  - Toma capturas de la tarjeta.
- [ ] **Step 4: Documenta** en `README.md`, en «Apuntes para una IA», qué pasa cuando cambia la cita y la acción de marcar resueltos en bloque.
- [ ] **Step 5: Commit** — «Los apuntes siguen al texto retocado y muestran cómo era y cómo quedó»

---

### Task 7: Marcar sin parar con `M`

**Files:**
- Modify: `src/Shoken.App/Assets/lector.js` (keydown, `notes.mark`, `notes.set` con `flashId`, tarjeta sin nota) y `lector.css`
- Modify: `src/Shoken.App/Services/DocumentView.cs` (`MarkSegmentAsync`, `SetNotesAsync`, `NoteRequest.Create`, `NoteRequest.MarkDuplicate`)
- Modify: `src/Shoken.App/MainWindow.xaml.cs` (`OnPreviewKeyDown`, `ExecuteCommand`)
- Modify: `src/Shoken.App/MainWindow.Notes.cs` (`MarkForReview`, `CreateNote`)
- Modify: `README.md` (tabla de Uso y «Apuntes para una IA»)

**Interfaces:**
- Produces:
  - `NoteRequest.Create(string Quote, string StartAnchor, string EndAnchor, bool Quiet = false)`. `Quiet` sale de `quiet: true` en `noteCreate`.
  - `NoteRequest.MarkDuplicate`, del mensaje `{type:'noteMarkDuplicate'}`.
  - `DocumentView.MarkSegmentAsync(string anchor)`: `Task`, sobre `window.lector.notes.mark(anchor)`. **No** enfoca el WebView.
  - `DocumentView.SetNotesAsync(IEnumerable<DocumentNote> notes, bool visible, string? editId = null, string? flashId = null)`. La página destella `flashId` después de dibujar.
  - `MainWindow.MarkForReview()`.

Comportamiento (spec, sección 3):

1. **La tecla `M`** (sin modificadores) manda el comando `mark`:
   - En `lector.js`, si el foco no está en `input`, `textarea`, `[contenteditable]` ni `.lector-note-ui`.
   - En `OnPreviewKeyDown`, si `Keyboard.Modifiers == None` y `e.OriginalSource is not TextBoxBase`.
   - Busca con grep en los XAML que ninguna tecla de acceso o atajo use ya `M` sola.
2. **`MarkForReview`:**
   - Si `_player.CurrentSegmentId < 0` (o no hay documento), muestra `ShowToast("Empieza a leer para marcar un párrafo.", ok: false)`.
   - Si no, llama a `MarkSegmentAsync(ancla del segmento)`.
3. **`notes.mark(anchor)`:**
   - Si ya hay un apunte pendiente cuya cita plegada es igual al texto plegado del bloque, manda `noteMarkDuplicate`.
   - Si no, manda `noteCreate` con la cita del bloque entero y `quiet: true`.
4. **`CreateNote` con `Quiet`:**
   - No abre el editor ni fuerza `ShowNotes`.
   - Llama a `SetNotesAsync(..., _settings.ShowNotes, editId: null, flashId: note.Id)`.
   - Muestra el aviso «Marcado para revisar», o «Marcado para revisar (los apuntes están ocultos)» si `!_settings.ShowNotes`.
5. **`MarkDuplicate`:** `ShowToast("Ese párrafo ya estaba marcado.")`.
6. **La tarjeta de un apunte sin nota** (fuera de edición) muestra «Por revisar · sin nota».

- [ ] **Step 1: Implementa.**
- [ ] **Step 2: Compila y corre las pruebas.** Run: `dotnet build -c Release` y `dotnet test`. Expected: sin errores; PASS.
- [ ] **Step 3: Verifica con CDP** (puerto 9333):
  1. Empieza a leer (comando `toggle`).
  2. Despacha `keydown` con `key: 'm'` sobre `document` por `Runtime.evaluate`. Aparece una tarjeta nueva sin nota con «Por revisar · sin nota», la lectura sigue (`State` Playing) y el foco no se movió a la tarjeta.
  3. Una segunda `m` sobre el mismo párrafo no crea otra tarjeta.
  4. `m` con el foco en la barra de búsqueda (`Ctrl+F` abierta) escribe la letra y no marca.
  5. «Copiar para la IA» incluye la marca con «(sin nota)».
- [ ] **Step 4: Documenta** en `README.md`: la fila `M` en la tabla de Uso y una línea en «Apuntes para una IA».
- [ ] **Step 5: Commit** — «Marca para revisar el párrafo que suena con la tecla M, sin parar la lectura»

---

### Task 8: Cobertura en Core

**Files:**
- Create: `src/Shoken.Core/Coverage/HeardTracker.cs`
- Create: `src/Shoken.Core/Coverage/DocumentCoverage.cs` (con `HeardBlock` y `HeardSpoken`)
- Create: `src/Shoken.Core/Coverage/CoverageStore.cs`
- Create: `src/Shoken.Core/Settings/DocumentFiles.cs`
- Modify: `src/Shoken.Core/Notes/NoteStore.cs` (usa `DocumentFiles`, mismo nombre de archivo que hoy)
- Modify: `src/Shoken.Core/Settings/AppSettings.cs` (`AppPaths.HeardDirectory`)
- Modify: `src/Shoken.Core/Model/ReadingTimeline.cs`
- Test: `tests/Shoken.Core.Tests/CoverageTests.cs`, `tests/Shoken.Core.Tests/ReadingTimelineTests.cs`

**Interfaces:**
- Consumes: `SpeechSegment.BlockKey` e `IsAnnouncement` (Tarea 1).
- Produces:
  - **`public sealed class HeardTracker`**, que decide cuándo un segmento sonó completo y sin saltos:
    - `public event Action<int>? SegmentHeard`
    - `public void Started(int segmentId, bool fromBeginning)`: actual = id; desde el principio = `fromBeginning`.
    - `public void Moved(int segmentId)`: si id == actual, no hace nada. Si id == actual+1 y el actual empezó desde el principio, emite `SegmentHeard(actual)`. Después, actual = id, y «desde el principio» = (id == anterior+1).
    - `public void Jumped(int landedSegmentId)`: actual = id; desde el principio = false.
    - `public void Reset()`: actual = -1.
    - `public void Finished()`: emite `SegmentHeard(actual)` si empezó desde el principio y luego hace `Reset()`.
  - `public enum HeardSpoken { Full, Announcement }`
  - `public sealed record HeardBlock(string Key, HeardSpoken Spoken)`
  - **`public sealed class DocumentCoverage(SpeechDocument document, IEnumerable<HeardBlock> stored)`**:
    - `public bool MarkSegmentHeard(int segmentId)`: true si con eso un bloque quedó escuchado y antes no lo estaba. Un bloque queda escuchado cuando todos sus segmentos se marcaron en esta sesión, o desde el inicio si su `BlockKey` está en `stored`.
    - `public bool IsBlockHeard(string anchorId)`
    - `public IReadOnlyCollection<string> HeardAnchors { get; }`
    - `public IReadOnlySet<int> HeardSegments { get; }`: los segmentos de los bloques escuchados.
    - `public IReadOnlyList<HeardBlock> ToStored()`: solo los bloques del documento actual. `Spoken` es `Announcement` si todos los segmentos del bloque son `IsAnnouncement`.
    - `public void Clear()`
  - **`public sealed class CoverageStore(string directory)`**, con el formato `{ "Document": ruta, "Blocks": [ { "Key", "Spoken" } ] }` (enum como texto):
    - `Load(string documentPath)`: `IReadOnlyList<HeardBlock>`. Un archivo dañado o inexistente da una lista vacía.
    - `Save(string documentPath, IReadOnlyList<HeardBlock> blocks)`: una lista vacía borra el archivo. Escribe un temporal y lo mueve, como `NoteStore`.
    - `Delete(string documentPath)`.
  - `internal static class DocumentFiles` con `public static string PathFor(string directory, string documentPath)`. Es el nombre que hoy calcula `NoteStore.FileFor`: SHA-256 de la ruta completa en mayúsculas, 20 hex en minúscula + `.json`.
  - `AppPaths.HeardDirectory => Path.Combine(Root, "escuchado")`
  - `public sealed record TimelineSpan(TimeSpan Start, TimeSpan Duration)`. En `ReadingTimeline`:
    - `public IReadOnlyList<TimelineSpan> SpansOf(IReadOnlySet<int> segments)`: une los segmentos consecutivos.
    - `public TimeSpan HeardDuration(IReadOnlySet<int> segments, int fromSegment, int toSegmentExclusive)`.

- [ ] **Step 1: Escribe las pruebas que fallan** en `CoverageTests`:
  - `HeardTracker_CountsASegmentWhenPlaybackMovesToTheNextOne`: `Started(3, true)` y `Moved(4)` dan [3].
  - `HeardTracker_DoesNotCountTheFirstSegment_WhenStartedInTheMiddle`: `Started(3, false)`, `Moved(4)` y `Moved(5)` dan [4].
  - `HeardTracker_DoesNotCountASegmentSkippedWithTenSeconds`: `Started(3, true)`, `Jumped(3)`, `Moved(4)` y `Moved(5)` dan [4]. Además, `Started(3, true)`, `Jumped(5)`, `Moved(6)` y `Moved(7)` dan [6].
  - `HeardTracker_CountsTheLastSegment_WhenTheDocumentEnds`: `Started(8, true)` y `Finished()` dan [8].
  - `HeardTracker_DoesNotCount_AfterStopping`: `Started(2, true)`, `Reset()` y `Moved(3)` dan [].
  - `Coverage_BlockIsHeardOnlyWhenAllItsPartsWereHeard`: un párrafo en 3 partes (`LongSegmentChars = 80`). Marcar la 0 y la 1 da false; marcar la 2 da true y el bloque queda escuchado.
  - `Coverage_StaysHeard_AfterReopeningTheSameDocument`: `ToStored()` alimenta una `DocumentCoverage` nueva sobre el mismo texto, y `IsBlockHeard` da true.
  - `Coverage_IsLost_OnlyForTheEditedBlock`: con v2 editando el segundo párrafo, el primero sigue escuchado y el segundo no.
  - `Coverage_SurvivesMovingABlock`: un párrafo escuchado movido al final sigue escuchado.
  - `Coverage_StoresAnnouncement_ForAnAnnouncedCodeBlock`: un bloque de código en Announce da `HeardSpoken.Announcement`; un párrafo da `Full`.
  - `CoverageStore_RoundTrips_AndTreatsADamagedFileAsEmpty`
  - `CoverageStore_SavingNothing_DeletesTheFile`
  - `DocumentFiles_GivesNotesTheSameFileNameAsBefore`: `DocumentFiles.PathFor(dir, @"C:\docs\a.md")` es igual al valor que da hoy `NoteStore` (calcúlalo una vez y fíjalo en la prueba).
  - En `ReadingTimelineTests`: `Timeline_SpansOf_MergesConsecutiveSegments` y `Timeline_HeardDuration_SumsOnlyHeardSegmentsInRange`, siguiendo cómo esas pruebas arman la línea de tiempo.
- [ ] **Step 2: Corre y verifica que fallan.** Run: `dotnet test --filter "FullyQualifiedName~CoverageTests|FullyQualifiedName~ReadingTimelineTests"`. Expected: FAIL.
- [ ] **Step 3: Implementa** las clases. `NoteStore` usa `DocumentFiles.PathFor`.
- [ ] **Step 4: Corre todas las pruebas.** Run: `dotnet test`. Expected: PASS, incluidas las de `NotesTests`.
- [ ] **Step 5: Commit** — «Agrega el registro de lo escuchado por bloque»

---

### Task 9: Color de lo escuchado en los temas

**Files:**
- Modify: `src/Shoken.Core/Settings/ThemePalettes.cs` (`PaletteColors` y los 8 colores; `DocumentVariables`)
- Modify: `src/Shoken.App/Services/ThemeService.cs`
- Modify: `src/Shoken.App/Assets/lector.css` (`--heard` de respaldo en `:root` y en el bloque oscuro)
- Test: `tests/Shoken.Core.Tests/ThemeTests.cs`

**Interfaces:**
- Produces:
  - `PaletteColors`: un parámetro nuevo **al final**, `string Heard`.
  - `DocumentVariables(...)["heard"] = c.Heard`.
  - `ThemeService`: `Set("LectorHeardBrush", c.Heard)`.

- [ ] **Step 1: Escribe las pruebas que fallan**
  - `Palettes_HeardIsSubtleButVisible` (Theory, `EveryPaletteAndMode`):
    - `Ratio(c.Heard, c.Document)` está entre 1.3 y 2.5, ambos incluidos.
    - `Ratio(c.Heard, c.Track) >= 1.15`, para distinguirse sobre la línea de tiempo.
  - `DocumentVariables_IncludeHeard`: `DocumentVariables(c, dark)["heard"] == c.Heard`.
  - Si `Palettes_ClassicIsTheOriginalDesign` compara el record entero, actualízala con el valor nuevo de Clásico.
- [ ] **Step 2: Corre y verifica que fallan.** Run: `dotnet test --filter "FullyQualifiedName~ThemeTests"`. Expected: FAIL.
- [ ] **Step 3: Elige los 8 colores** (cada tema en claro y oscuro), del tono del tema y que pasen la prueba. Como referencia, en claro la luminancia queda entre la del borde y la del texto secundario; en oscuro, un poco más clara que la pista (`Track`). No cambies ningún color existente ni bajes ningún umbral.
- [ ] **Step 4: Corre todas las pruebas.** Run: `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** — «Agrega a los temas el color tenue de lo escuchado»

---

### Task 10: Cobertura visible

**Files:**
- Modify: `src/Shoken.App/Services/ReadAloudController.cs`
- Create: `src/Shoken.App/MainWindow.Coverage.cs` (partial)
- Modify: `src/Shoken.App/MainWindow.xaml.cs` (`ActivateAsync`, `RefreshOutline`, `RebuildTimeline`, cierre de pestaña y salida) y `MainWindow.xaml` (plantilla de la fila del índice)
- Modify: `src/Shoken.App/Services/DocumentTab.cs` (`OutlineItem`)
- Modify: `src/Shoken.App/Controls/PlayerBar.xaml.cs`
- Modify: `src/Shoken.App/MainWindow.TabActions.cs` (menú de la pestaña)
- Modify: `src/Shoken.App/Services/DocumentView.cs`, `src/Shoken.App/Assets/lector.js`, `lector.css`
- Modify: `README.md`, `AGENTS.md`

**Interfaces:**
- Consumes: `HeardTracker`, `DocumentCoverage`, `CoverageStore`, `AppPaths.HeardDirectory`, `ReadingTimeline.SpansOf`/`HeardDuration`, `TimelineSpan` (Tarea 8). `LectorHeardBrush` y `--heard` (Tarea 9).
- Produces:
  - **`ReadAloudController`:** `public event Action<int>? SegmentHeard`, reenviado de un `HeardTracker` interno.
    - `RunAsync`, después de `Stop()`: `Started(startSegmentId, intoSegment <= TimeSpan.Zero)`.
    - `UpdateCurrentSegment`, al cambiar de id: `Moved(id)`, salvo durante un salto.
    - `TrySeekWithinCurrent`: marca que está saltando alrededor de su `UpdateCurrentSegment(target)` y después llama a `Jumped(_currentSegmentId)`.
    - `Stop()`: `Reset()`.
    - `Finish()`: `Finished()` antes de limpiar `_currentSegmentId`.
  - `DocumentView.SetHeardAsync(IEnumerable<string> anchors)`: `Task`, sobre `window.lector.coverage.set(anchors)`. Pone la clase `lector-heard` en todos los `[data-seg]` de esas anclas y la quita del resto.
  - `PlayerBar.SetHeard(IReadOnlyList<TimelineSpan> spans)`: dibuja los tramos con `LectorHeardBrush` dentro de la pista de cada capítulo, entre el fondo y el relleno del avance. Se vuelve a aplicar después de cada `SetTimeline`.
  - `OutlineItem` recibe `double heardFraction` (0 a 1) y expone:
    - `HeardFraction`, para una línea de 2 px bajo la fila con `ScaleX` = fracción.
    - `HeardCheckVisibility`: ✓ en `LectorText2Brush` junto a la duración cuando la fracción ≥ 0.999.

**Comportamiento** (spec, sección 4; solo archivos, no pegados):

- **Al mostrar un documento** en `ActivateAsync`: `new DocumentCoverage(document, store.Load(path))`. Después llama a `SetHeardAsync`, pinta el índice y `SetHeard` de la línea de tiempo.
- **En `SegmentHeard(id)`:** si `MarkSegmentHeard(id)` da true, actualiza la página, el índice y la línea de tiempo, y programa el guardado (un `DispatcherTimer` de 2 s).
- **Guardado inmediato** al cambiar o cerrar la pestaña y al salir de la app.
- **Fracción de una fila del índice:** se calcula sobre los mismos capítulos con que `RefreshOutline` calcula su duración. Para cada capítulo, sus segmentos van de su `FirstSegmentId` al `FirstSegmentId` del siguiente capítulo (o al final del documento). La fracción es `HeardDuration` / duración.
- **Menú de la pestaña** (solo archivos): «Olvidar lo escuchado» hace `Clear()` y `store.Delete(path)`, y actualiza la vista.
- **La línea en el documento:**
  - `.lector-heard`: 2 px de `var(--heard)` en el margen izquierdo, a la izquierda de la barra de lectura (que está a −10 px; por ejemplo, a −24 px).
  - Tiene que verse bien en párrafos, títulos, `li` (sin tapar la viñeta), filas de tabla (sin romper la tabla), `pre`, citas y alertas.
  - Convive con `.lector-reading`.

- [ ] **Step 1: Implementa.**
- [ ] **Step 2: Compila y corre las pruebas.** Run: `dotnet build -c Release` y `dotnet test`. Expected: sin errores; PASS.
- [ ] **Step 3: Verifica con CDP** (puerto 9333) sobre una copia de `docs/ejemplo.md`:
  - Lee desde el principio unos segundos: los primeros bloques ganan `lector-heard` y el resto no.
  - Salta con `forward10` sobre un párrafo: ese no gana la clase.
  - Cierra y reabre Shoken: la cobertura sigue (`escuchado\` tiene el archivo).
  - Edita un párrafo escuchado desde PowerShell y recarga: ese pierde la clase y los demás la conservan.
  - Toma capturas del documento con la línea en un párrafo, un `li`, una fila de tabla y un `pre`, del índice con el progreso y el ✓, y de la línea de tiempo. Hazlo en tema Papel claro y oscuro.
  - Prueba «Olvidar lo escuchado».
- [ ] **Step 4: Documenta.**
  - `README.md`: en Uso, cómo se ve lo escuchado y «Olvidar lo escuchado». En «Datos que guarda», la fila `escuchado\` («Qué bloques de cada documento ya se escucharon, un archivo por documento»).
  - `AGENTS.md`: una regla en «Reglas que no se ven a simple vista»:
    - Un bloque cuenta como escuchado cuando sus segmentos suenan de principio a fin sin saltos. Lo decide `HeardTracker`, alimentado por `ReadAloudController`.
    - Se reconoce por `SpeechSegment.BlockKey`, que es el hash del Markdown fuente del bloque, no del texto para la voz.
    - Se guarda qué sonó (`HeardSpoken`) para que el futuro diseño de cambios aplique su propia regla.
- [ ] **Step 5: Commit** — «Muestra lo ya escuchado en el documento, el índice y la línea de tiempo»
