# Abrir documentos de Word (.docx) — Diseño

Fecha: 2026-10-26. Estado: revisado por un constructor; pendiente de que el usuario apruebe el documento.

Las decisiones del usuario van sin marca. Las que tomó Claude y el usuario todavía no revisó van marcadas
**(decisión de Claude)**.

## Para qué

Hoy Shoken solo abre Markdown y texto. El usuario quiere que también lea documentos de Word, en dos usos
por igual:

- **Documentos que recibe** (informes, contratos, especificaciones): escucharlos y quizá dejar apuntes.
- **Sus propios documentos mientras los escribe en Word**: oír cómo suenan, dejar apuntes, pasárselos a una IA,
  pegar en Word el texto que la IA propone, guardar y volver a escuchar.

Un éxito: el usuario arrastra un `.docx` a Shoken y lo escucha con la misma entonación, resaltado, apuntes y marcas
de lo escuchado que un `.md`. Edita en Word, guarda, y Shoken recarga solo, sin perder lo escuchado de los
párrafos que no tocó.

## Objetivos

1. Abrir `.docx` arrastrándolo a la ventana, con el diálogo *Abrir* y pegando su ruta (Ctrl+V), igual que un `.md`.
2. Convertirlo a Markdown en memoria y pasárselo a la cadena de siempre (parser, planificador, SSML, caché), sin que
   esa cadena sepa que vino de Word.
3. Mostrar sus imágenes en el documento, en su lugar.
4. Recargar solo cuando el usuario guarda en Word, con las mismas reglas que un `.md` (`ReloadPolicy`).
5. Que los apuntes y lo escuchado funcionen igual que en un `.md`, y que la exportación de apuntes tenga sentido
   para un Word: sección y cita, sin números de línea.

## Fuera de alcance

- **Otros formatos**: `.doc` (el binario viejo), `.rtf`, `.odt` y PDF. Solo `.docx`. Se pueden agregar después
  detrás de la misma pieza de conversión.
- **«Abrir con» del Explorador.** No se registra `.docx` ni en `FileAssociation` ni en `AppxManifest.xml`: Shoken no
  aparece en el menú del Explorador para los Word. Solo se llega arrastrando, con *Abrir* o pegando la ruta.
- **Editar el Word desde Shoken** o que la IA edite el `.docx`. El usuario pega a mano el texto que la IA propone.
- **Guardar un `.md` junto al Word.** La conversión vive solo en memoria (salvo las imágenes; ver abajo).
- **Comentarios de Word:** no se leen ni se muestran. DocSharp tiene `ProcessCommentReference`: el constructor
  verifica que no salgan y, si salen, los apaga. **Revisiones sin aceptar:** se lee como si estuvieran aceptadas
  (sin el texto borrado). DocSharp parece saltarse `DeletedRun` y `DeletedText`; hay que verificarlo con una
  prueba **(decisión de Claude)**.

## Lo que no debe pasar

- **Que el mismo `.docx` dé otro Markdown entre una apertura y otra.** El `BlockKey` (lo escuchado) es el hash del
  Markdown de cada bloque, y la clave de caché sale del SSML. Si la conversión no es estable, se pierde lo escuchado
  y se vuelve a gastar cuota de Azure. Nada que dependa de la máquina, de la hora o de la ruta puede quedar dentro
  del Markdown (por ejemplo, la ruta absoluta de una imagen).
- **Que actualizar DocSharp cambie la salida sin que nadie se entere.** La versión queda fija y una prueba con la
  salida esperada lo detecta.
- **Tocar `SsmlBuilder`, el normalizador o `ChunkPlanner`.** La conversión va antes del parser; los `.md` siguen
  dando exactamente el mismo SSML.
- **Bloquear la ventana al abrir.** Convertir tarda de 0,4 a 0,8 s (medido en frío); no se hace en el hilo de la UI.
- **Que el encabezado, el pie o el número de página de Word se lean** al principio o al final de cada documento.
- **Dejar al usuario sin aviso** si el `.docx` está dañado, protegido con contraseña o no se puede convertir.

## Cómo funciona

### La librería

**DocSharp.Docx 0.18.1** (MIT, de Manfredi Marceca, `DocxToMarkdownConverter`), con la versión fija en
`Shoken.Core.csproj`. Trae `DocumentFormat.OpenXml` y suma unos 9 MB a la app. Se eligió frente a las otras con
una comparación sobre los mismos archivos (2026-10-26):

| Librería | Por qué no |
|---|---|
| Aspose.Words.FOSS (MIT) | Casi igual de buena, pero el *Título* de Word queda como texto, deja un número de página suelto y pesa 31 MB |
| ManagedCode.MarkItDown (MIT) | Pierde las viñetas de las listas, rompe las tablas y pierde las citas; pesa 61 MB por los SDK de nube que arrastra |
| AllToMarkdown | Su licencia solo permite uso personal y exige regalías para publicar. Usa DocSharp por dentro y da la misma salida |
| MarkItDown (Python), Pandoc | Python o GPL; ver la regla de `AGENTS.md` sobre no redistribuir GPL |

Lo que se vio en la prueba y que hay que arreglar al convertir:

1. **Encabezado y pie:** salen al principio y al final. Se apagan con `ExportHeaderFooter = false` (verificado).
2. **Notas al pie:** salen como `[1]` en el texto y `[1]: texto` al final. Markdig toma eso como una definición de
   enlace: el texto de la nota desaparece y el `[1]` queda como enlace. Hay que pasarlos a `[^1]` y `[^1]: texto`,
   que el parser ya lee como notas al pie. DocSharp escapa los corchetes del texto común (`\(opcional\)` salió
   escapado), así que un `[n]` sin escapar debería ser siempre una nota. **El constructor tiene que verificarlo**
   con un documento que tenga un `[1]` escrito a mano.
3. **Imágenes:** DocSharp las extrae a `ImagesOutputFolder` con nombres estables (`image1.png`), pero en el
   Markdown pone la ruta absoluta (`file:///C:/…/image1.png`) y, como texto alternativo, el identificador interno
   (`rId4`) en vez del que tiene el Word. Ver la sección de imágenes.

### Dónde va

Una pieza nueva en `Shoken.Core` (por ejemplo `Documents/WordDocument`; el nombre lo decide el constructor) que
recibe la ruta de un `.docx` y la carpeta para las imágenes, y devuelve el Markdown ya corregido. No depende de WPF.

La App la usa en los tres lugares donde hoy lee el archivo como texto: `ReadDocumentAsync`, la carga de la pestaña
en `MainWindow.xaml.cs` (`File.ReadAllTextAsync(tab.State.Path)`) y `CopyMarkdown`. Si es `.docx`, convierte; si no,
lee como hasta ahora. La conversión corre en un hilo aparte (`Task.Run`).

`ReadDocumentAsync` mantiene su comportamiento: lee con `FileShare.ReadWrite | FileShare.Delete` y reintenta si el
archivo está tomado. Se verificó que se puede leer un `.docx` mientras Word lo tiene abierto.

### Imágenes

- Se guardan en una sola carpeta de los datos de la app, `AppPaths.WordImagesDirectory` (`imagenes-word\`), con el
  **SHA-256 de su contenido** como nombre (`<sha256>.png`). Nunca junto al `.docx`. Así el nombre depende solo de la
  imagen: el mismo Word da el mismo Markdown desde cualquier ruta o máquina, y una recarga que no se aplica no pisa
  las imágenes que la página está mostrando. La carpeta no se limpia sola, como la caché de audio
  **(decisión de Claude, al planificar; reemplaza la carpeta por documento y la carpeta temporal de la recarga)**.
- La carpeta de imágenes se sirve con un **host virtual aparte**, no con `doc.lector`: `doc.lector` sigue apuntando a
  la carpeta del `.docx`, porque `ResolveDocumentLink` busca ahí los enlaces relativos a otros archivos. En el
  Markdown la imagen va como `https://img.lector/<sha256>.<ext>`. El host no puede cambiar después, porque queda en
  el `BlockKey` **(decisión de Claude, a partir de la revisión)**.
- El texto alternativo es el que tiene la imagen en Word (`docPr/@descr`, o `@title` si falta). Si no tiene ninguno,
  va vacío, nunca `rId…` **(decisión de Claude)**. Se lee solo si está activada la opción de siempre
  (`ReadImageAltText`).
- **Imágenes que el navegador no muestra** (EMF y los gráficos de Excel o Visio pegados) y **gráficos nativos de
  Word** (que no son imágenes): DocSharp los **descarta sin avisar** (se probó el 2026-10-26: ni el EMF ni el gráfico
  aparecen en su salida). Por eso se detectan antes de convertir, en el XML del documento: un dibujo cuyo
  `graphicData` es un `c:chart`, o cuya imagen tiene un tipo que el navegador no muestra (todo salvo PNG, JPEG, GIF,
  BMP, WebP y SVG). En su lugar va una imagen fija de la app, `https://app.lector/imagen-no-disponible.svg`, que dice
  «Imagen que Shoken no puede mostrar · Ábrela en Word», con el texto alternativo del Word como `alt` (se lee en voz
  alta con la regla de siempre, `ReadImageAltText`, y se ve al pasar el mouse) **(decisión de Claude, al
  planificar)**. No se agrega un convertidor de EMF. Al cerrar la pestaña se puede borrar; el
  constructor decide si se borra al cerrar, al salir o se deja como caché.

### Recarga

`DocumentWatcher` vigila el `.docx` como a cualquier archivo. Su filtro es el nombre exacto del archivo, así que
ignora el `~$documento.docx` que crea Word, y ya atiende `Renamed` (Word guarda reemplazando el archivo). Al llegar
el aviso, `MainWindow` vuelve a convertir y compara **el Markdown convertido** con el cargado; `ReloadPolicy.Decide`
decide como siempre. Lo escuchado se conserva en los párrafos que no cambiaron, porque su Markdown sale igual.

### Apuntes

Funcionan sin cambios: se anclan por su cita y se guardan por el hash de la ruta (`NoteStore`). Lo único que
cambia es la exportación (`NoteExporter`):

- Con un `.docx`, la referencia de cada apunte **no lleva `archivo:líneas`**: son líneas del Markdown convertido,
  que en Word no significan nada. Va la sección y la cita.
- La instrucción por omisión (`DefaultInstruction`) habla de «archivo:líneas». Para un Word hace falta una variante
  que diga «sección» y pida el texto propuesto para pegarlo en Word. Cómo se elige la variante, y qué pasa si el
  usuario personalizó la plantilla en Configuración, lo decide el constructor.
- `NoteSource` necesita saber que el documento es un Word (por ejemplo, por la extensión de `FilePath`).
- **Tarjetas en la página:** en un Word, la tarjeta no muestra la línea («L12», que hoy pone `lector.js` con
  `'L' + n.shownLine` y el título «Línea en el Markdown»). No va nada en su lugar. La App le avisa a la página que el
  documento es un Word (el cómo lo decide el constructor). En los `.md` todo sigue igual.

### Menú de la pestaña

- **«Abrir en el editor»:** con un `.docx` lo abre con el programa que Windows tenga para `.docx`
  (`UseShellExecute = true`, normalmente Word), en vez de VS Code. El aviso dice «Abierto en Word · al guardar, se
  recarga solo» **(decisión de Claude)**.
- **«Copiar el texto Markdown»:** copia el Markdown convertido, no el archivo **(decisión de Claude)**. Puede usar el
  que ya está cargado. Si convierte, lo hace en segundo plano: hoy `CopyMarkdown` lee de forma síncrona en el hilo
  de la UI.
- El resto («Abrir carpeta contenedora», «Recargar», «Olvidar lo escuchado», copiar ruta o nombre) queda igual.

### Abrir

- `DraggedFiles.OpenableExtensions` agrega `.docx`: el aviso de la capa de arrastre y el de «no se puede abrir» se
  actualizan solos, porque se arman con esa lista.
- El filtro del diálogo *Abrir* agrega «Word (*.docx)» y lo suma al grupo de documentos que se pueden abrir
  **(decisión de Claude)**.
- Las sesiones guardan la ruta como siempre; al restaurar se vuelve a convertir.

### Errores

Si la conversión falla (archivo dañado, protegido con contraseña, no es un `.docx` de verdad), se escribe en
`ErrorLog` y se avisa con el mismo estilo de los otros errores de lectura, por ejemplo «No se pudo abrir el
documento de Word.» **(decisión de Claude)**. La pestaña no se queda a medio cargar.

## Pruebas (Shoken.Core.Tests)

- **Salida esperada:** un `.docx` de muestra en las pruebas, con título, encabezados, listas con viñetas y
  numeradas, tabla, cita, negrita, cursiva, nota al pie, una imagen con texto alternativo, encabezado y número de
  página. Se compara contra el Markdown esperado. Si se actualiza DocSharp y la salida cambia, esta prueba falla.
  Arreglarla es una decisión consciente, porque invalida lo escuchado y la caché de los Word ya abiertos.
- **Estabilidad:** convertir dos veces el mismo archivo, desde dos rutas distintas, da el mismo Markdown.
- **Notas al pie:** `[n]` pasa a `[^n]`, y un `\[1\]` escrito a mano queda como texto.
- **Imágenes:** ruta estable y texto alternativo real; vacío si el Word no lo tiene. Una EMF y un gráfico nativo
  dan su aviso.
- **Revisiones y comentarios:** el texto borrado sin aceptar no sale; los comentarios no salen.
- **Exportación de apuntes:** con un `.docx`, sin `archivo:líneas`, con sección y cita.
- El `.docx` de muestra se arma en la prueba con `DocumentFormat.OpenXml` o se versiona como archivo; decide el
  constructor.

## Documentación

- `README.md`: que Shoken abre Word y cómo (arrastrar, *Abrir*, pegar la ruta); que no aparece en «Abrir con» para
  los `.docx`.
- `AGENTS.md`: una regla nueva con la versión fija de DocSharp, por qué no se actualiza a la ligera (cambia
  `BlockKey` y la caché), las tres correcciones a su salida y que las imágenes van a `AppPaths`, no junto al archivo.

## Lo que queda al constructor

- Nombres de las clases y carpetas nuevas.
- Cómo se implementan las correcciones a la salida de DocSharp: opciones del convertidor, recorrer el documento con
  OpenXml o arreglar el texto después.
- Cómo se elige la instrucción de exportación para Word.
