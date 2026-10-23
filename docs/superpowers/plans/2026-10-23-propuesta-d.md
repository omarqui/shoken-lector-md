# Propuesta D (Fluent + reproductor abajo, tema claro y oscuro) — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Llevar la app al diseño "D": pestañas en la barra de título, barra de comandos liviana, índice con duración por sección, reproductor abajo con línea de tiempo por capítulos y toda la información de lectura, y tema claro / oscuro / del sistema.

**Architecture:** La lógica nueva que se puede probar va a `LectorMd.Core`: duración real de cada bloque de audio (guardada en la caché) y una línea de tiempo del documento que mezcla duraciones reales y estimadas. La interfaz se reorganiza en WPF: `WindowChrome` para poner las pestañas en la barra de título, un `PlayerBar` (UserControl) que reemplaza la barra de reproducción de arriba y la barra de estado, y el tema con `Application.ThemeMode` + `CoreWebView2Profile.PreferredColorScheme`.

**Tech Stack:** .NET 10, WPF con tema Fluent (`ThemeMode`), WebView2, xUnit.

**Spec:** el lienzo https://claude.ai/artifact/64CbmmAYZ1hcieGNGYv6y8, tableros "D · Fluent + reproductor abajo" (claro y oscuro), y las decisiones de abajo.

## Global Constraints

- Textos de la interfaz, comentarios y `<summary>` en español; identificadores en inglés (ver `AGENTS.md`).
- No cambiar el SSML ni la normalización: la clave de caché de audio no puede cambiar en este plan (cero cuota extra).
- Las entradas viejas de la caché (sin duración) siguen sirviendo: se leen y su duración se estima.
- Colores de tema (de los tableros): claro `bg #F3F3F3`, `surface #FFFFFF`, `surface2 #F0F0F0`, `border #E5E5E5`, `text #1B1B1B`, `text2 #5D5D5D`, acento `#005FB8`; oscuro `bg #202020`, `surface #2B2B2B`, `surface2 #363636`, `border #3D3D3D`, `text #FFFFFF`, `text2 #C8C8C8`, acento `#60CDFF` con texto `#00344D` encima. Usar los recursos del tema Fluent de WPF donde existan (`ApplicationBackgroundBrush`, `CardStrokeColorDefaultBrush`, `AccentFillColorDefaultBrush`, …) y estos valores solo donde el tema no tenga equivalente.
- Velocidades del selector: `0.75`, `1`, `1.25`, `1.5`, `1.75`. Una `PlaybackSpeed` guardada que no coincida se ajusta a la más cercana.
- Estimación de duración: 15 caracteres por segundo de `PlainText`, más `PauseBeforeMs` y `PauseAfterMs` de cada segmento.
- Atajos existentes se conservan (Espacio, Esc, Ctrl+←/→, Ctrl+O/T/W/Tab/B/V/,, F5). Nuevo: ninguno.

## Decisiones (mías, sobre lo que el diseño no dice)

- **Capítulos de la línea de tiempo** = las entradas del índice (h1–h3). Lo que hay antes del primer título es un capítulo "Inicio" solo si dura más de 0.
- **Mensajes de estado**: no hay barra de estado. `ShowStatus` escribe en la segunda línea del bloque de información del reproductor y la "fija" 8 s; durante la lectura, esa línea vuelve a "Párrafo N de M · capítulo X de Y".
- **Tema**: `AppSettings.Theme` = `System | Light | Dark`. El botón sol/luna alterna Claro ↔ Oscuro (desde Sistema pasa al opuesto del actual). En Configuración, un combo con las tres opciones.
- **Botones de ventana** propios (minimizar, maximizar/restaurar, cerrar) con Segoe Fluent Icons `E921`, `E922`/`E923`, `E8BB`. Se pierde el menú de Snap Layouts al pasar sobre maximizar (ver Review Focus).

## Review Focus

- **Documento sin títulos**: la línea de tiempo muestra un solo tramo "Documento" y el índice su mensaje vacío; nada se rompe ni divide entre cero.
- **Ventana maximizada**: con `WindowChrome` el contenido no debe quedar cortado por los bordes (margen de 7 px al maximizar) y las pestañas siguen clicables; doble clic en el área libre de la barra maximiza/restaura.
- **Cambio de tema con un documento abierto**: el visor (WebView2) cambia de colores sin recargar ni perder el párrafo ni el scroll.
- **Duraciones mixtas**: la línea de tiempo mezcla bloques en caché (duración real) y no generados (estimada); al terminar de generarse un bloque, el total se corrige sin saltos raros del tiempo transcurrido (el transcurrido nunca retrocede mientras suena el mismo segmento).
- **Pestaña vacía o bienvenida**: el reproductor queda deshabilitado con línea de tiempo vacía y el texto "Abre o pega un documento".

---

### Task 1: Duración real de cada bloque de audio

**Files:**
- Modify: `src/LectorMd.Core/Tts/TtsAbstractions.cs` (`AudioChunk`)
- Modify: `src/LectorMd.Core/Caching/AudioCache.cs` (`Store`, `TryGet`, `CacheEntry`)
- Modify: `src/LectorMd.Core/Tts/AzureTtsEngine.cs:118`, `src/LectorMd.Core/Tts/PiperTtsEngine.cs:119`
- Test: `tests/LectorMd.Core.Tests/AudioCacheAndUsageTests.cs`

**Interfaces:**
- Produces: `AudioChunk.Duration : TimeSpan?` (null = desconocida); `AudioCache.Store(string key, byte[] audio, string extension, IReadOnlyList<(int Index, TimeSpan Offset)> marks, SpeechChunk chunk, string engineName, TimeSpan? duration)`; `CacheEntry` gana `long? DurationTicks = null` como último parámetro posicional.

- [ ] **Step 1: Tests que fallan**
  - `Cache_RoundTripsDuration`: `Store(..., duration: TimeSpan.FromSeconds(12.5))` y luego `TryGet` → `Duration == 12.5 s`.
  - `Cache_OldEntriesWithoutDuration_StillLoad`: escribir a mano `{"Extension":".mp3","Marks":[{"Index":0,"OffsetTicks":0}]}` + el `.mp3` → `TryGet` no es null y `Duration` es null.
- [ ] **Step 2:** `dotnet test --filter AudioCacheAndUsageTests` → FAIL (no compila: falta el parámetro).
- [ ] **Step 3: Implementar.** Azure pasa `result.AudioDuration`; Piper pasa `combined.Duration` (`WavAudio`).
- [ ] **Step 4:** `dotnet test` → todo en verde.
- [ ] **Step 5:** commit `Guarda la duración de cada bloque de audio en la caché`.

### Task 2: Línea de tiempo del documento

**Files:**
- Create: `src/LectorMd.Core/Model/ReadingTimeline.cs`
- Test: `tests/LectorMd.Core.Tests/ReadingTimelineTests.cs`

**Interfaces:**
- Consumes: `DocumentOutline.Build` (existe), `SpeechChunk`, `AudioChunk.Duration` y `AudioChunk.Marks` (Task 1).
- Produces:
  - `public sealed record TimelineChapter(string Title, string? AnchorId, int FirstSegmentId, TimeSpan Start, TimeSpan Duration, bool IsEstimated)`
  - `public sealed class ReadingTimeline` con `static ReadingTimeline Build(SpeechDocument document, IReadOnlyList<SpeechChunk> chunks, Func<SpeechChunk, AudioChunk?> cached)`, `TimeSpan Total`, `IReadOnlyList<TimelineChapter> Chapters`, `TimeSpan StartOf(int segmentId)`, `TimeSpan DurationOf(int segmentId)`, `int ChapterIndexOf(int segmentId)`, `TimeSpan ElapsedAt(int segmentId, TimeSpan intoSegment)` (acota `intoSegment` a `[0, DurationOf]`).
- Duración por segmento: si el bloque está en caché con `Duration`, la diferencia entre marcas consecutivas (la última hasta `Duration`); si no, la estimación de Global Constraints. Un capítulo es estimado si alguno de sus segmentos lo es.

- [ ] **Step 1: Tests que fallan** (`ReadingTimelineTests`)
  - `Estimate_UsesCharactersAndPauses`: un segmento de 150 caracteres con `PauseAfterMs = 300` y sin caché → `DurationOf == 10.3 s`.
  - `CachedChunk_UsesRealMarksAndDuration`: bloque de 2 segmentos con marcas 0 s y 4 s y `Duration` 9 s → `DurationOf(0) == 4 s`, `DurationOf(1) == 5 s`, `IsEstimated == false`.
  - `Chapters_FollowTheOutline_AndAddInicioOnlyIfItHasContent`: `"Intro.\n\n# A\n\nTexto.\n\n# B\n\nMás."` → capítulos `["Inicio", "A", "B"]`; sin el "Intro." → `["A", "B"]`.
  - `NoHeadings_IsOneChapterNamedDocumento`: `"Solo texto."` → un capítulo `"Documento"` con `Start == 0` y `Duration == Total`.
  - `ElapsedAt_IsStartPlusClampedOffset`: `ElapsedAt(id, 1 h) == StartOf(id) + DurationOf(id)`.
- [ ] **Step 2:** `dotnet test --filter ReadingTimelineTests` → FAIL (no existe la clase).
- [ ] **Step 3: Implementar `ReadingTimeline`.**
- [ ] **Step 4:** `dotnet test` → verde.
- [ ] **Step 5:** commit `Agrega la línea de tiempo del documento con duraciones reales y estimadas`.

### Task 3: Tema y pasos de velocidad en la configuración

**Files:**
- Modify: `src/LectorMd.Core/Settings/AppSettings.cs`
- Create: `src/LectorMd.Core/Settings/PlaybackSpeeds.cs`
- Test: `tests/LectorMd.Core.Tests/TabsAndOutlineTests.cs` (o un `SettingsTests.cs` nuevo)

**Interfaces:**
- Produces: `public enum AppTheme { System, Light, Dark }`; `AppSettings.Theme : AppTheme` (por defecto `System`); `public static class PlaybackSpeeds { public static IReadOnlyList<double> Steps { get; } /* 0.75,1,1.25,1.5,1.75 */ public static double Nearest(double value); }`.

- [ ] **Step 1: Tests que fallan:** `Nearest(1.1) == 1.0`, `Nearest(1.4) == 1.5`, `Nearest(3) == 1.75`, `Nearest(0.1) == 0.75`; un `settings.json` sin `Theme` se deserializa como `System` (usar `JsonSerializer` con `JsonStringEnumConverter` como `SettingsStore`).
- [ ] **Step 2:** FAIL. **Step 3:** implementar. **Step 4:** verde. **Step 5:** commit `Agrega el tema y los pasos de velocidad a la configuración`.

### Task 4: Tema claro, oscuro y del sistema en la app

**Files:**
- Modify: `src/LectorMd.App/App.xaml` (quitar `ThemeMode="System"` fijo), `src/LectorMd.App/App.xaml.cs`
- Create: `src/LectorMd.App/Services/ThemeService.cs`
- Modify: `src/LectorMd.App/Services/DocumentView.cs` (nuevo `SetColorScheme(AppTheme)`), `src/LectorMd.App/SettingsWindow.xaml(.cs)` (combo "Tema"), `src/LectorMd.App/MainWindow.xaml(.cs)` (botón sol/luna)

**Interfaces:**
- Consumes: `AppTheme`, `AppSettings.Theme` (Task 3).
- Produces: `internal static class ThemeService { public static void Apply(AppTheme theme); public static bool IsDark { get; } public static event Action? Changed; }` — `Apply` fija `Application.Current.ThemeMode` (`System`/`Light`/`Dark`); `IsDark` resuelve `System` leyendo `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`. `DocumentView.SetColorScheme(AppTheme)` fija `CoreWebView2.Profile.PreferredColorScheme` (`Auto`/`Light`/`Dark`); `lector.css` ya responde a `prefers-color-scheme`.

- [ ] **Step 1:** implementar `ThemeService`, aplicar `_settings.Theme` en `App.OnStartup` antes de crear la ventana y en `DocumentView.InitializeAsync`.
- [ ] **Step 2:** botón sol/luna en la barra de comandos (icono según `IsDark`; `aria`: `AutomationProperties.Name` "Cambiar a tema oscuro"/"claro") → alterna, guarda, `Apply`, `SetColorScheme`. Combo "Tema" en Configuración (Sistema, Claro, Oscuro).
- [ ] **Step 3: Verificar** con la app de Debug y UI Automation: alternar el tema con un documento abierto no recarga la página (el `StatusText`/párrafo y el `ScrollY` de la pestaña no cambian) y `settings.json` guarda `"Theme": "Dark"`.
- [ ] **Step 4:** `dotnet build` sin errores; commit `Agrega tema claro, oscuro y del sistema`.

### Task 5: Pestañas en la barra de título

**Files:**
- Modify: `src/LectorMd.App/MainWindow.xaml(.cs)`
- Create: `src/LectorMd.App/Controls/CaptionButtons.xaml(.cs)` (minimizar, maximizar/restaurar, cerrar)

**Interfaces:**
- Consumes: la `TabList` y `OnNewTabClick` existentes.
- Produces: nada que usen otras tareas.

- [ ] **Step 1:** `WindowChrome` en `MainWindow` (`CaptionHeight` = alto de la fila de pestañas, `ResizeBorderThickness` 6, `GlassFrameThickness` 0, `UseAeroCaptionButtons` false). Fila superior: icono + "Lector MD", `TabList`, botón "+", espacio libre arrastrable, `CaptionButtons`. `WindowChrome.IsHitTestVisibleInChrome="True"` en la lista de pestañas, el "+" y los botones.
- [ ] **Step 2:** margen de 7 px al root cuando `WindowState == Maximized` (`StateChanged`); el icono de maximizar alterna `E922`/`E923`.
- [ ] **Step 3: Verificar** con UI Automation: los botones de ventana existen y responden a `InvokePattern` (minimizar → `WindowState Minimized`; maximizar → `Maximized` y de vuelta); seleccionar una pestaña sigue activándola; el título de la ventana se mantiene para la barra de tareas.
- [ ] **Step 4:** commit `Mueve las pestañas a la barra de título`.

### Task 6: Reproductor abajo con línea de tiempo e información

**Files:**
- Create: `src/LectorMd.App/Controls/PlayerBar.xaml(.cs)`
- Modify: `src/LectorMd.App/Services/ReadAloudController.cs` (evento de progreso)
- Modify: `src/LectorMd.App/MainWindow.xaml(.cs)` (quitar transporte y velocidad de arriba y la barra de estado; añadir `Pegar`; índice con duración)
- Modify: `src/LectorMd.App/Services/DocumentTab.cs` (`OutlineItem` gana `Time : string`)

**Interfaces:**
- Consumes: `ReadingTimeline` (Task 2), `PlaybackSpeeds` (Task 3), `HybridTtsEngine.TryGetCached(SpeechChunk)` (existe), `AudioChunk.FromCache` y `EngineName` (existen).
- Produces:
  - `ReadAloudController.ProgressChanged : event Action<int /*segmentId*/, TimeSpan /*intoSegment*/>?`, disparado desde el `DispatcherTimer` existente como mucho cada 250 ms; `intoSegment = _player.Position - _current.OffsetOf(segmentId)`.
  - `PlayerBar` con: `SetTimeline(ReadingTimeline? timeline)`, `SetProgress(TimeSpan elapsed, int chapterIndex)`, `SetPosition(string section, string detail)`, `ShowNotice(string text)` (fija 8 s), `SetEngine(string name, bool fromCache, bool fallback, string? reason)`, `SetUsage(int used, int limit, (long Characters, DateTime At)? azure)`, `SetSpeed(double)`, `SetState(PlaybackState)`; eventos `PlayPauseClicked`, `StopClicked`, `PreviousClicked`, `NextClicked`, `SpeedChanged(double)`, `RefreshUsageClicked`, `ChapterClicked(string? anchorId)`.
  - Línea de tiempo: un `ItemsControl` horizontal con un `Grid` cuyas columnas son estrellas proporcionales a `Duration.TotalSeconds` de cada capítulo; en cada celda una barra de 6 px (relleno = parte escuchada) y el nombre (11 px, recortado). Clic en un tramo = `ChapterClicked(anchor)` → leer desde ese título. Tiempos `mm:ss` a los lados (transcurrido y `−restante`); con estimados, el total lleva `≈`.
  - Info de voz: "Dalia · Azure" + segunda línea "desde caché · sin gastar cuota" / "generando con Azure" / "Piper (respaldo): <motivo>", con el punto de color ok/ámbar/rojo actual (`StatusBrushes`).

- [ ] **Step 1:** `ProgressChanged` en el controlador.
- [ ] **Step 2:** `PlayerBar` con los colores del tema (recursos dinámicos del tema Fluent) y el diseño de los tableros D.
- [ ] **Step 3:** en `MainWindow`: construir la línea de tiempo al activar una pestaña y recalcularla en `OnChunkStarted` cuando el bloque no venía de caché (ya tiene duración real); conectar eventos; mover `ShowStatus` a `PlayerBar.ShowNotice`; índice con la duración de cada capítulo y "leyendo" en el actual.
- [ ] **Step 4: Verificar** con UI Automation sobre la app de Debug y `docs/ejemplo.md`: los nombres de los tramos coinciden con el índice; el texto del total existe; al pulsar ⟳ el aviso aparece en el reproductor; con una pestaña vacía el botón de reproducir está deshabilitado y dice "Abre o pega un documento".
- [ ] **Step 5:** commit `Mueve el reproductor abajo con línea de tiempo por capítulos`.

### Task 7: Documentación y verificación final

**Files:**
- Modify: `README.md` (sección Uso: tema, reproductor, línea de tiempo), `AGENTS.md` (estructura: `Controls/`, `ReadingTimeline`)

- [ ] **Step 1:** actualizar documentos.
- [ ] **Step 2:** `dotnet test` (todo verde) y `dotnet build -c Release` sin errores (cerrar antes la app abierta).
- [ ] **Step 3:** abrir la app en Release con tus pestañas y revisar en claro y oscuro; commit y push.
