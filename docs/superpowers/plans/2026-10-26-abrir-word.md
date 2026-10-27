# Abrir documentos de Word (.docx) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shoken abre `.docx` (arrastrar, *Abrir*, pegar la ruta) y lo lee como un `.md`: el Word se convierte a
Markdown en memoria antes del parser.

**Architecture:** Una pieza nueva en Core, `Documents/WordConverter`, convierte un `.docx` (stream) en Markdown
estable: preprocesa el XML con OpenXml (gráficos y EMF → marcador, texto alternativo real), convierte con DocSharp y
corrige la salida (notas al pie, imágenes con nombre por SHA-256 en `imagenes-word\`). La App la llama donde hoy lee
el archivo como texto, sirve las imágenes con el host `img.lector` y ajusta el menú, la exportación y las tarjetas de
apuntes.

**Tech Stack:** .NET 10, `DocSharp.Docx` 0.18.1 (MIT; trae `DocumentFormat.OpenXml`), xUnit, WPF + WebView2.

**Spec:** `docs/superpowers/specs/2026-10-26-abrir-word-design.md`

## Global Constraints

- `DocSharp.Docx` fijo en **0.18.1** (`Version="0.18.1"`, sin rangos). Nada de Aspose, MarkItDown, AllToMarkdown ni Pandoc.
- Solo `.docx`. No se toca `FileAssociation` ni `AppxManifest.xml` (Shoken no aparece en «Abrir con» para `.docx`).
- No se tocan `SsmlBuilder`, el normalizador ni `ChunkPlanner`; los `.md` dan exactamente el mismo SSML que hoy.
- El Markdown de un Word no puede contener nada que dependa de la máquina, la ruta o la hora.
- Imágenes: `AppPaths.WordImagesDirectory` = `Path.Combine(Root, "imagenes-word")`; URL `https://img.lector/<sha256 en hex minúsculas>.<ext>`.
- Aviso de imagen no mostrable: `https://app.lector/imagen-no-disponible.svg`, con el texto «Imagen que Shoken no puede mostrar · Ábrela en Word».
- Tipos que se muestran: `image/png`, `image/jpeg`, `image/gif`, `image/bmp`, `image/webp`, `image/svg+xml`. Todo lo demás, y los `c:chart`, van como aviso.
- Error de conversión: `InvalidDataException("No se pudo abrir el documento de Word: puede estar dañado o protegido con contraseña.")`.
- Texto en la interfaz, comentarios y nombres de pruebas en español; identificadores en inglés; clases `sealed`; pruebas `Componente_Comportamiento`.
- La conversión nunca corre en el hilo de la UI (`Task.Run`).
- `dotnet test` en verde antes de cerrar cada tarea.

## Review Focus

1. **Un `[1]` escrito a mano en el Word** debe quedar como texto, no volverse nota al pie → prueba en la Tarea 1 (`Word_LiteralBracketNumber_StaysText`).
2. **Guardar en Word mientras Shoken lee** (Word reemplaza el archivo; un instante no existe o está tomado) → la recarga reintenta como hoy y no cierra la pestaña; se verifica a mano en la Tarea 5.
3. **Un `.docx` dañado o con contraseña** → mensaje claro y la pestaña no queda a medio cargar → prueba en la Tarea 1 (`Word_CorruptFile_ThrowsInvalidData`) y verificación a mano en la Tarea 5.
4. **La misma imagen dos veces, o el mismo Word en otra carpeta**, da el mismo Markdown → prueba en las Tareas 1 y 2 (`Word_SameFileFromTwoPaths_GivesSameMarkdown`, `Word_SameImageTwice_IsStoredOnce`).
5. **Un texto alternativo con `]`, `*` o saltos de línea** no rompe la sintaxis de la imagen → prueba en la Tarea 2 (`Word_AltTextWithMarkdownCharacters_IsEscaped`).

---

### Task 1: WordConverter — estructura, notas al pie, encabezado y pie

**Files:**
- Modify: `src/Shoken.Core/Shoken.Core.csproj` (agregar `<PackageReference Include="DocSharp.Docx" Version="0.18.1" />`)
- Create: `src/Shoken.Core/Documents/WordConverter.cs`
- Create: `tests/Shoken.Core.Tests/Fixtures/Word/crear-muestras.ps1` (genera los `.docx` con Word por COM, invisible; se versionan los `.docx` resultantes)
- Create: `tests/Shoken.Core.Tests/Fixtures/Word/estructura.docx`, `estructura.expected.md`, `especiales-texto.docx`
- Modify: `tests/Shoken.Core.Tests/Shoken.Core.Tests.csproj` (copiar `Fixtures\**` a la salida: `<None Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />`)
- Test: `tests/Shoken.Core.Tests/WordConverterTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class WordConverter(string imagesDirectory)` en `Shoken.Core.Documents`
  - `public static bool IsWordFile(string path)`: extensión `.docx`, sin distinguir mayúsculas
  - `public string Convert(Stream docx)`: Markdown estable. Lanza `InvalidDataException` (mensaje de Global Constraints) si el archivo no se puede abrir o convertir
  - `public const string WordImagesHost = "img.lector";` y `public const string UnavailableImageUrl = "https://app.lector/imagen-no-disponible.svg";`

El script de muestras parte de `scratchpad\scripts\crear-muestras.ps1` de la sesión de diseño. Guárdalo **con BOM**:
PowerShell 5.1 lee como ANSI un `.ps1` sin BOM y rompe los acentos del Word. Arma:
- `estructura.docx`: título (estilo *Título*), H1/H2, lista con viñetas, lista numerada, tabla 4×3 con encabezado en negrita, cita (estilo *Cita*), negrita, cursiva, una nota al pie, encabezado de página «Shoken - documento de prueba» y número de página en el pie.
- `especiales-texto.docx`: un párrafo con «Ver el punto [1] del contrato.» escrito a mano; un párrafo con una revisión de borrado sin aceptar (`TrackRevisions = true`, borrar la palabra «BORRADO»); un comentario de Word cuyo texto es «COMENTARIO».

- [ ] **Step 1: Write the failing tests** en `WordConverterTests.cs` (helper `Convert(string fixture)` que abre `Fixtures/Word/<fixture>` y usa un `imagesDirectory` temporal por prueba):
  - `Word_Structure_MatchesExpected`: `Assert.Equal(File.ReadAllText("estructura.expected.md"), Convert("estructura.docx"))`
  - `Word_HeaderFooterAndPageNumber_AreNotRead`: la salida no contiene «Shoken - documento de prueba» ni termina en una línea que sea solo un número
  - `Word_Footnotes_UseMarkdigSyntax`: contiene `[^1]` y una línea que empieza con `[^1]: Esta es la nota al pie.`; no contiene `\n[1]:`; y `new MarkdownSpeechParser().Parse(md)` tiene un segmento cuyo `PlainText` contiene «Esta es la nota al pie»
  - `Word_LiteralBracketNumber_StaysText`: el párrafo parseado de `especiales-texto.docx` dice «Ver el punto [1] del contrato.» y la salida no contiene `[^`
  - `Word_TrackedDeletion_IsNotRead`: no contiene «BORRADO»
  - `Word_Comments_AreNotRead`: no contiene «COMENTARIO»
  - `Word_SameFileFromTwoPaths_GivesSameMarkdown`: copia `estructura.docx` a dos carpetas temporales distintas; las dos conversiones son iguales
  - `Word_CorruptFile_ThrowsInvalidData`: un `MemoryStream` con bytes al azar lanza `InvalidDataException` con el mensaje exacto
  - `Word_IsWordFile_OnlyDocx`: `true` para `C:\a.docx` y `C:\A.DOCX`; `false` para `.doc`, `.md` y `.docxx`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WordConverterTests`
Expected: FAIL de compilación (`WordConverter` no existe)

- [ ] **Step 3: Implement `WordConverter.Convert(Stream)`**

Copia el stream a un `MemoryStream` y abre `WordprocessingDocument.Open(stream, isEditable: true)`; un
`OpenXmlPackageException`, un `FileFormatException` o un `InvalidDataException` se convierten en la
`InvalidDataException` de Global Constraints. Convierte con `DocxToMarkdownConverter { ExportHeaderFooter = false }`
(si `ConvertToString` no acepta un stream, guárdalo en un archivo temporal de `Path.GetTempPath()` y bórralo en un
`finally`). Después pasa las notas al pie a la sintaxis de Markdig, en este orden:

```csharp
// Definiciones al final: "[1]:  texto" → "[^1]: texto"
md = Regex.Replace(md, @"(?m)^\[(\d+)\]:\s*", "[^$1]: ");
// Referencias en el texto: un [n] sin escapar (DocSharp escapa los corchetes del texto común)
md = Regex.Replace(md, @"(?<!\\)\[(\d+)\](?![:(\[])", "[^$1]");
```

Si `Word_LiteralBracketNumber_StaysText` falla porque DocSharp **no** escapa los corchetes del texto, no
debilites la prueba: antes de convertir, cambia en el XML los `[`/`]` de los `w:t` por un marcador alfanumérico y
devuélvelos escapados (`\[`, `\]`) después de convertir. Si una revisión borrada o un comentario aparecen, quítalos
del XML antes de convertir (los `w:del` y los `w:commentReference`, `w:commentRangeStart` y `w:commentRangeEnd`).

- [ ] **Step 4: Genera `estructura.expected.md`**

Corre la conversión una vez, **revisa a mano** que tenga título `#`, H1/H2, viñetas `-`, lista `1.`, la tabla con `| --- |`,
la cita `>` y la nota `[^1]`, sin encabezado ni número de página, y guárdala como `estructura.expected.md` (UTF-8 sin
BOM, `\n`).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS, todas (las de antes también)

- [ ] **Step 6: Commit**

```bash
git add src/Shoken.Core tests/Shoken.Core.Tests
git commit -m "WordConverter: convierte .docx a Markdown con DocSharp (estructura y notas al pie)"
```

### Task 2: WordConverter — imágenes, texto alternativo y avisos

**Files:**
- Modify: `src/Shoken.Core/Documents/WordConverter.cs`
- Modify: `tests/Shoken.Core.Tests/Fixtures/Word/crear-muestras.ps1` (+ `imagenes.docx`, `especiales.docx`)
- Test: `tests/Shoken.Core.Tests/WordConverterTests.cs`

**Interfaces:**
- Consumes: `WordConverter(string imagesDirectory)`, `Convert(Stream)`, las constantes de la Tarea 1
- Produces: después de `Convert`, `imagesDirectory` contiene `<sha256>.<ext>` por cada imagen mostrable

Muestras:
- `imagenes.docx`: un PNG con texto alternativo «Logo de Shoken»; el mismo PNG otra vez, sin texto alternativo; un JPEG con texto alternativo `Paso [2] *importante*` y un salto de línea.
- `especiales.docx`: un EMF con texto alternativo «Diagrama EMF», un gráfico nativo de Word (`InlineShapes.AddChart2`) con texto alternativo «Gráfico de ventas», y un EMF sin texto alternativo. En la sesión de diseño se comprobó que DocSharp descarta los dos sin dejar rastro.

- [ ] **Step 1: Write the failing tests**
  - `Word_Image_IsStoredByContentHash_WithItsAltText`: la salida contiene `![Logo de Shoken](https://img.lector/<sha>.png)`, donde `<sha>` es el SHA-256 en hexadecimal minúsculo del PNG de la muestra, y ese archivo existe en `imagesDirectory`
  - `Word_ImageWithoutAltText_HasEmptyAlt`: contiene `![](https://img.lector/<sha>.png)` y no contiene `rId`
  - `Word_SameImageTwice_IsStoredOnce`: `imagesDirectory` tiene exactamente 2 archivos (PNG y JPEG)
  - `Word_AltTextWithMarkdownCharacters_IsEscaped`: el documento parseado con `ReadImageAltText = true` tiene un segmento con «Imagen: Paso [2] *importante*», y el texto alternativo no rompe la imagen (el `HtmlBody` tiene 3 `<img`)
  - `Word_EmfAndChart_BecomeTheUnavailableNotice`: en `especiales.docx` hay exactamente 3 apariciones de `(https://app.lector/imagen-no-disponible.svg)`, con alt «Diagrama EMF», «Gráfico de ventas» y vacío, en ese orden y entre los párrafos que las rodean en el Word
  - `Word_NoFileUrls`: ninguna salida contiene `file:`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WordConverterTests`
Expected: FAIL (alt `rId…`, rutas `file:///`, avisos ausentes)

- [ ] **Step 3: Implement**

Antes de convertir, recorre los `w:drawing` del cuerpo (`MainDocumentPart.Document.Body.Descendants<Drawing>()`):
- texto alternativo = `wp:docPr/@descr`, o `@title` si falta, o `""`;
- si su `a:graphicData` tiene un `c:chart`, o su `a:blip/@r:embed` apunta a un `ImagePart` cuyo `ContentType` no está en la lista de Global Constraints, reemplaza el `w:drawing` por un `w:t` con el marcador `SHOKENAVISO{n}Z` (alfanumérico, para que DocSharp no lo escape) y guarda `n → alt`;
- si no, guarda `rId → alt` (el primero gana si el mismo `rId` se repite).

Convierte con `ImagesOutputFolder` en una carpeta temporal nueva, y bórrala en un `finally`. Después:
- cada `![rIdN](file:///…/imageK.ext)` → calcula el SHA-256 del archivo extraído, cópialo a `imagesDirectory/<sha>.<ext>` si no existe y reescribe como `![alt](https://img.lector/<sha>.<ext>)`;
- cada `SHOKENAVISO{n}Z` → `![alt](https://app.lector/imagen-no-disponible.svg)`.

En el alt: los saltos de línea pasan a espacio, y `\ [ ] * _` se escapan con `\`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shoken.Core tests/Shoken.Core.Tests
git commit -m "WordConverter: imágenes por SHA-256 con su texto alternativo y aviso para EMF y gráficos"
```

### Task 3: Qué se puede abrir, y la exportación de apuntes de un Word

**Files:**
- Modify: `src/Shoken.Core/Desktop/DraggedFiles.cs:8` (`OpenableExtensions` agrega `".docx"` al final)
- Modify: `src/Shoken.Core/Settings/AppSettings.cs` (`AppPaths.WordImagesDirectory`, con su `<summary>`, al lado de `HeardDirectory`; crearla en `EnsureCreated`)
- Modify: `src/Shoken.Core/Notes/NoteExporter.cs`
- Test: `tests/Shoken.Core.Tests/DraggedFilesTests.cs`, `tests/Shoken.Core.Tests/NotesTests.cs`

**Interfaces:**
- Consumes: `WordConverter.IsWordFile`
- Produces: `NoteExporter.DefaultWordInstruction` (const string); `ToMarkdown` mantiene su firma y decide con `WordConverter.IsWordFile(source.FilePath)`

`DefaultWordInstruction` es `DefaultInstruction` con estos cambios: «(archivo:líneas y sección)» pasa a «(sección)»;
se agrega «El documento es un Word: no lo puedes editar. Devuélveme el texto propuesto listo para pegarlo en Word.»
antes de «Responde apunte por apunte…»; y el párrafo de «Cita original»/«Ahora dice» queda igual. Si el usuario tiene
su propia plantilla (`instruction` no vacío), se usa la suya también con Word, pero sin líneas.

- [ ] **Step 1: Write the failing tests**
  - `DraggedFilesTests.Dragged_NothingOpenable_CannotDrop_AndSaysWhatOpens`: actualiza el `Detail` esperado a `"Shoken abre archivos .md, .markdown, .mdx, .txt y .docx"`
  - `Openable_AcceptsWordDocx`: `IsOpenable(@"C:\informe.DOCX")` es `true`; `IsOpenable(@"C:\viejo.doc")` es `false`
  - `Export_WordDocument_HasSectionAndQuoteButNoLines`: con `new NoteSource(@"C:\Docs\informe.docx", "Informe")` y `Note("cita", 12, section: "Uso")`, contiene `"## 1. Aclarar · § Uso"`, no contiene `"informe.docx:12"` y empieza con la `DefaultWordInstruction` ya completada
  - `Export_WordDocument_WithCustomInstruction_UsesItWithoutLines`: con la instrucción `"Revisa {archivo}"`, empieza con `@"Revisa C:\Docs\informe.docx"` y no contiene `":12"`
  - `Settings_WordImages_LiveInTheAppData`: `new AppPaths(_dir).WordImagesDirectory == Path.Combine(_dir, "imagenes-word")`

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DraggedFilesTests|FullyQualifiedName~NotesTests"`
Expected: FAIL

- [ ] **Step 3: Implement** los tres cambios. Los mensajes «no es un documento Markdown» de `DraggedFiles` quedan igual **(decisión de Claude: un PDF tampoco es un Word, y el detalle ya lista lo que sí se abre)**.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shoken.Core tests/Shoken.Core.Tests
git commit -m "Abrir .docx: extensión aceptada, carpeta de imágenes y exportación de apuntes sin líneas"
```

### Task 4: La App lee los Word

**Files:**
- Modify: `src/Shoken.App/MainWindow.xaml.cs` (`_word`, carga en `:479`, `ReadDocumentAsync` en `:1401`, filtro del diálogo en `:1109`)
- Modify: `src/Shoken.App/MainWindow.TabActions.cs` (`OpenInEditor` en `:219`, `CopyMarkdown` en `:242`)
- Modify: `src/Shoken.App/Services/DocumentView.cs` (mapear `img.lector` en `InitializeAsync`, `:100`; agregar `imagen-no-disponible.svg` a la lista de `WriteAssets`, `:580`)
- Modify: `src/Shoken.App/Services/HtmlPages.cs:22` (`img-src https://doc.lector https://img.lector https: data:`)
- Create: `src/Shoken.App/Assets/imagen-no-disponible.svg` (+ `EmbeddedResource` en `Shoken.App.csproj`, como `lector.css`)

**Interfaces:**
- Consumes: `WordConverter`, `WordConverter.IsWordFile`, `AppPaths.WordImagesDirectory`
- Produces: `private async Task<string> LoadDocumentTextAsync(string path)` en `MainWindow`: lanza si no puede, y lo usan la carga y `CopyMarkdown`

- [ ] **Step 1: Implementa la lectura**
  - `private readonly WordConverter _word = new(_paths.WordImagesDirectory);` (o donde se cree `_paths`).
  - `LoadDocumentTextAsync(path)`: si `IsWordFile`, lee los bytes con `FileShare.ReadWrite | FileShare.Delete` y devuelve `await Task.Run(() => _word.Convert(new MemoryStream(bytes)))`. Si no, el `File.ReadAllTextAsync` de hoy. La línea 479 pasa a `markdown ??= await LoadDocumentTextAsync(tab.State.Path);`: el `catch` de ahí ya muestra el `ex.Message` y cierra la pestaña.
  - `ReadDocumentAsync`: para un Word, el mismo bucle de reintentos lee los bytes y después convierte en `Task.Run`. Si la conversión lanza `InvalidDataException`, lo escribe en `ErrorLog` y devuelve `null` (la pestaña sigue con lo último cargado, como cuando el archivo está tomado).
  - Filtro del diálogo: `"Documentos (*.md;*.markdown;*.mdx;*.txt;*.docx)|*.md;*.markdown;*.mdx;*.txt;*.docx|Markdown (*.md;*.markdown;*.mdx)|*.md;*.markdown;*.mdx|Word (*.docx)|*.docx|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*"`.

- [ ] **Step 2: Imágenes en la página**
  - `public const string WordImagesHost = WordConverter.WordImagesHost;` en `HtmlPages`, CSP actualizada, y `SetVirtualHostNameToFolderMapping(HtmlPages.WordImagesHost, _paths.WordImagesDirectory, Allow)` en `InitializeAsync`.
  - `imagen-no-disponible.svg`: un recuadro de 320×120 con borde punteado, el ícono de imagen tachada y el texto «Imagen que Shoken no puede mostrar · Ábrela en Word», en colores neutros que se lean en tema claro y oscuro (gris medio sobre transparente).

- [ ] **Step 3: Menú de la pestaña**
  - `OpenInEditor(path)`: si `IsWordFile`, `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })` y el aviso `"Abierto en Word · al guardar, se recarga solo"`. Si falla, el aviso de error de hoy.
  - `CopyMarkdown` pasa a `async void`/`Task`: si la pestaña es la activa y `_loadedMarkdown` no es null, copia ese; si no, `await LoadDocumentTextAsync(path)`. El `catch` agrega `InvalidDataException`.

- [ ] **Step 4: Compila y prueba**

Run: `dotnet build -c Release` y `dotnet test`
Expected: compila sin advertencias nuevas; pruebas PASS

- [ ] **Step 5: Commit**

```bash
git add src/Shoken.App
git commit -m "La app abre .docx: conversión en segundo plano, imágenes en img.lector y menú para Word"
```

### Task 5: Tarjetas de apuntes sin línea en un Word, y verificación a mano

**Files:**
- Modify: `src/Shoken.App/Services/DocumentView.cs:223` (`SetNotesAsync(..., bool showLines = true)`)
- Modify: `src/Shoken.App/MainWindow.Notes.cs` (los cinco llamados pasan `showLines: !WordConverter.IsWordFile(path)`; los textos pegados, `true`)
- Modify: `src/Shoken.App/Assets/lector.js:935` (`set(items, show, editId, flashId, showLines)`) y `:1055`/`:1070`

**Interfaces:**
- Consumes: `WordConverter.IsWordFile`

- [ ] **Step 1: Implementa**

`set` guarda `showLines` (si llega `undefined`, `true`). En `cardHtml`: con `n.range` y `showLines`, igual que hoy; con
`n.range` y sin `showLines`, el `note-where` no se dibuja; sin `n.range`, «ya no está» como hoy.

- [ ] **Step 2: Verificación a mano** (memoria del proyecto: sin mouse ni teclado simulados; manejar `lector.js` por DevTools en el puerto 9333 y después reiniciar sin el puerto). Con `tests/Shoken.Core.Tests/Fixtures/Word/*.docx`, comprueba:
  1. Abrir `estructura.docx` con *Abrir* y pegando su ruta: se ve igual que su `.expected.md` y se lee con la entonación de un `.md`.
  2. `imagenes.docx`: se ven las imágenes; `especiales.docx`: tres avisos en su lugar.
  3. Un apunte en un Word: la tarjeta no muestra «L…»; «Copiar para la IA» no lleva `:línea`. En un `.md` sigue mostrando «L…».
  4. Abrir una copia de `estructura.docx` en Word, cambiar un párrafo y guardar: Shoken recarga solo («Documento actualizado»), lo escuchado de los otros párrafos se conserva y la pestaña no se cierra. Guardar varias veces seguidas no muestra errores.
  5. Renombrar un `.txt` a `.docx` y abrirlo: aparece el mensaje de Global Constraints y no queda una pestaña rota.
  6. «Abrir en el editor» abre Word; «Copiar el texto Markdown» copia el Markdown convertido.

- [ ] **Step 3: Run tests**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add src/Shoken.App
git commit -m "Apuntes de un Word: la tarjeta no muestra la línea del Markdown"
```

### Task 6: Documentación

**Files:**
- Modify: `README.md` (qué se abre: agregar Word `.docx` por arrastrar, *Abrir* o pegar la ruta; que no aparece en «Abrir con»; que las EMF y los gráficos se muestran como aviso)
- Modify: `AGENTS.md` (en «Estructura» y en «Reglas que no se ven a simple vista»)

- [ ] **Step 1: Escribe la regla de `AGENTS.md`**: `Documents/WordConverter` convierte con `DocSharp.Docx` 0.18.1 fijo, porque actualizarlo cambia el Markdown y con él el `BlockKey` y la caché (`Word_Structure_MatchesExpected` falla: no la arregles regenerando el `.expected.md` sin revisar la diferencia). Incluye las correcciones a la salida (encabezado y pie apagados; `[n]` → `[^n]`; `rId` → texto alternativo real; gráficos y EMF → `imagen-no-disponible.svg`, porque DocSharp los descarta), que las imágenes viven en `AppPaths.WordImagesDirectory` por SHA-256 y se sirven con `img.lector`, y que `crear-muestras.ps1` necesita Word y guardarse con BOM.

- [ ] **Step 2: Run tests**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add README.md AGENTS.md docs/superpowers/specs/2026-10-26-abrir-word-design.md docs/superpowers/plans/2026-10-26-abrir-word.md
git commit -m "Documenta la apertura de Word"
```
