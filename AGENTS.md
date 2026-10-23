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
5. `Tts/HybridTtsEngine` decide el motor: caché → Azure (`AzureTtsEngine`) → Piper (`PiperTtsEngine`).
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
- **Colores del diseño en dos lugares:** `ThemeService.ApplyPalette` (recursos `Lector*Brush` de la ventana) y las variables de `Assets/lector.css` (el documento). Si cambias uno, cambia el otro.
- `ReadingTimeline` (Core) da la duración por segmento y capítulo: real para los bloques en caché (`AudioChunk.Duration`, guardada en la caché) y estimada (15 caracteres/s + pausas) para el resto.

## Estilo

- C# con `Nullable` e `ImplicitUsings` (ver `Directory.Build.props`). Clases `sealed` por defecto.
- Comentarios, `<summary>`, mensajes de la interfaz y nombres de pruebas descriptivos en **español**.
  Los identificadores de código van en inglés.
- Pruebas con nombre `Componente_Comportamiento` (por ejemplo `Ssml_IsValidXml_WithOneBookmarkPerSegment`).
- Si cambias cómo se lee un elemento de Markdown, actualiza la tabla "Cómo se lee cada elemento"
  del `README.md` y agrega el caso a `docs/ejemplo.md`.
