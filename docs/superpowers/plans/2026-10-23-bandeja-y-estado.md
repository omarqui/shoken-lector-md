# Bandeja, atajo global, mini reproductor y estado de Azure compacto — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Íconos de pestaña con color por tipo; la app sigue en la bandeja al cerrar, con instancia única; Ctrl+Alt+L lee el portapapeles desde cualquier app con un mini reproductor flotante; el estado de Azure y el consumo pasan a un componente compacto con un panel al pasar el mouse.

**Architecture:** Lo que se puede probar va a `LectorMd.Core`: el canal de instancia única (named pipe) y la fecha de reinicio del contador. La bandeja usa `System.Windows.Forms.NotifyIcon` con un `ContextMenu` de WPF (para que tome el tema Fluent); el atajo usa `RegisterHotKey` sobre el `HwndSource` de la ventana principal; el mini reproductor es una ventana WPF sin borde que la ventana principal alimenta con los mismos eventos que el `PlayerBar`.

**Tech Stack:** .NET 10, WPF (Fluent), WinForms solo para `NotifyIcon`, Win32 `RegisterHotKey`, xUnit.

**Spec:** la conversación del 2026-10-23 (diseño aprobado en chat) y las respuestas: atajo **Ctrl+Alt+L**; lo leído con el atajo **queda como pestaña pegada**; el mini reproductor se cierra a los **10 segundos** en pausa.

## Global Constraints

- Textos en español; identificadores en inglés (`AGENTS.md`). Sin cambios en SSML ni en la división en bloques (cero cuota extra).
- Colores nuevos van en `ThemeService.ApplyPalette`: `LectorPastedBrush` = `#8250DF` (claro) / `#C297FF` (oscuro). Ícono de pestaña `.md` = `LectorAccentBrush`.
- Atajo: `MOD_CONTROL | MOD_ALT | MOD_NOREPEAT` + `VK_L` (0x4C). Si `RegisterHotKey` falla, aviso en el reproductor: "Ctrl+Alt+L ya lo usa otra app: el atajo no está activo." y la app sigue.
- Mini reproductor: `Topmost`, `ShowActivated=False`, `ShowInTaskbar=False`, abajo a la derecha del área de trabajo con 12 px de margen, 380×118. Autocierre: 10 s en pausa, detenido o al terminar; la cuenta se detiene con el mouse encima.
- El mini reproductor aparece solo si la ventana principal está oculta o minimizada; se cierra cuando la ventana principal se muestra.
- Al cerrar con ✕: se oculta a la bandeja (y si estaba leyendo, sigue y aparece el mini reproductor). La primera vez, globo "Lector MD sigue en la bandeja. Ctrl+Alt+L lee lo que copiaste." (`AppSettings.TrayHintShown`). Solo *Salir* del menú de la bandeja termina la app.
- Instancia única por usuario: mutex `Local\LectorMd.Instancia` + pipe `LectorMd.Instancia.<usuario>`. La segunda instancia manda su archivo (o una línea vacía = "mostrarte") y termina.

## Review Focus

- **Atajo con el portapapeles vacío o con una imagen**: aviso en el mini reproductor/reproductor, sin crash ni pestaña vacía.
- **Ctrl+Alt+L mientras ya está leyendo**: se detiene lo actual y empieza lo nuevo (pestaña nueva); el mini reproductor no se duplica.
- **Salir desde la bandeja mientras lee**: se guarda la sesión, se libera el atajo y el ícono desaparece de la bandeja (sin "ícono fantasma").
- **Segunda instancia con un archivo** cuando la primera está oculta en la bandeja: la ventana aparece y el archivo se abre en su pestaña.
- **Hover del estado de Azure**: el panel no parpadea al pasar el mouse del componente al panel, y se cierra al salir de ambos.

---

### Task 1: Ícono de pestaña con color por tipo

**Files:** `src/LectorMd.App/Services/ThemeService.cs`, `src/LectorMd.App/Services/DocumentTab.cs` (`IsPasted` notificable), `src/LectorMd.App/MainWindow.xaml` (DataTrigger en el ícono).

- [ ] Paleta: `LectorPastedBrush`. `DocumentTab.IsPasted => State.IsPasted`, incluido en `NotifyChanged`.
- [ ] Ícono: estilo con `Foreground = LectorAccentBrush` y `DataTrigger IsPasted=True → LectorPastedBrush`.
- [ ] Verificar con captura (claro y oscuro): `.md` azul, pegada morado. Commit `Da color distinto al ícono de cada tipo de pestaña`.

### Task 2: Reproductor al estilo Spotify y estado de Azure compacto con panel al pasar el mouse

Pedido del usuario (captura con flechas): la sección y el párrafo a la **izquierda**, los controles de reproducción **al centro** y el estado y la velocidad a la derecha. La línea de tiempo por capítulos queda arriba, a todo el ancho, porque los nombres de los capítulos necesitan espacio. La fila de controles es un `Grid` de tres columnas `*`, `Auto`, `*`, para que el transporte quede centrado de verdad.

**Files:** `src/LectorMd.Core/Usage/UsageTracker.cs` (+ test), `src/LectorMd.App/Controls/PlayerBar.xaml(.cs)`, `src/LectorMd.App/MainWindow.xaml.cs` (`ShowEngine`, `UpdateUsage`).

**Interfaces:**
- Produces: `UsageTracker.ResetsOn : DateTime` (UTC, día 1 del mes siguiente). `PlayerBar.SetEngine(string name, string detail, Brush dot, string? reason)` y `PlayerBar.SetUsage(bool visible, int used, int limit, DateTime resetsOnUtc, string? azureReport)` (reemplazan las firmas actuales).

- [ ] **Test que falla:** `Usage_ResetsOnFirstDayOfNextMonth`: con `utcNow = 2026-10-23` → `2026-11-01`; con `2026-12-31` → `2027-01-01`.
- [ ] Implementar `ResetsOn`. Correr `dotnet test` → verde.
- [ ] `PlayerBar`: quitar `EngineChip` y `UsageBox`; nuevo `StatusButton` (botón plano: punto 8 px + barra 48×4 + porcentaje "4.9 %"), `AutomationProperties.Name` "Estado de Azure". `Popup` (`Placement=Top`, `StaysOpen=True`, `AllowsTransparency=True`) con: motor y voz, detalle (caché/generado/respaldo + motivo), "23 549 de 480 000 caracteres (4.9 %)", barra, "Se reinicia el 1 de noviembre", "Azure Monitor: 23 060 a las 01:01", botón "Consultar consumo real". Abre con `MouseEnter` del botón o clic/Enter; se cierra 250 ms después de que el mouse sale del botón y del panel.
- [ ] Verificar con captura del panel abierto (hover simulado con clic) y UIA: el porcentaje coincide con `uso-azure.json`. Commit `Junta el estado y el consumo de Azure en un componente compacto`.

### Task 3: Instancia única

**Files:** Create `src/LectorMd.Core/Ipc/SingleInstanceChannel.cs`, test `tests/LectorMd.Core.Tests/SingleInstanceTests.cs`; modify `src/LectorMd.App/App.xaml.cs`, `MainWindow.xaml.cs` (`BringToFrontAsync(string? file)`).

**Interfaces:**
- Produces: `public sealed class SingleInstanceChannel : IDisposable` con `static SingleInstanceChannel? TryClaim(string name)` (null si ya hay una instancia), `event Action<string?>? MessageReceived` (ruta o null = mostrarte), `static Task<bool> SendAsync(string name, string? message, TimeSpan timeout)`.

- [ ] **Tests que fallan:** `SecondClaim_Fails_WhileTheFirstIsAlive` (nombre único por prueba); `Send_DeliversThePath`; `Send_EmptyMeansShow` (llega null); `Send_ReturnsFalse_WhenNobodyListens`.
- [ ] Implementar (mutex + `NamedPipeServerStream` en bucle, una línea UTF-8 por conexión). `dotnet test` → verde.
- [ ] `App.OnStartup`: si `TryClaim` falla → `SendAsync(archivo)` y `Shutdown()`. Si no, `MessageReceived` → `Dispatcher` → `MainWindow.BringToFrontAsync(file)` (muestra, restaura, activa y abre el archivo).
- [ ] Verificar: lanzar el Debug dos veces (la segunda con `docs\ejemplo.md`) → queda un solo proceso y la pestaña se activa. Commit `Mantiene una sola instancia de la app`.

### Task 4: Bandeja del sistema

**Files:** `src/LectorMd.App/LectorMd.App.csproj` (`<UseWindowsForms>true</UseWindowsForms>` y `<Using Remove="System.Windows.Forms" />` para no chocar con WPF), Create `src/LectorMd.App/Services/TrayIcon.cs`, modify `MainWindow.xaml.cs`, `AppSettings` (`TrayHintShown`).

**Interfaces:**
- Produces: `internal sealed class TrayIcon : IDisposable` con `TrayIcon(Action open, Action readClipboard, Action exit)`, `ShowHint(string text)`. Ícono dibujado en tiempo de ejecución (cuadrado redondeado con el acento + glifo de altavoz `E767`), menú WPF `ContextMenu` con *Abrir Lector MD*, *Leer portapapeles (Ctrl+Alt+L)*, separador, *Salir*.

- [ ] `OnClosing`: si no se pidió salir → `e.Cancel = true`, `SaveSession()`, `Hide()`, globo la primera vez, y si está leyendo → mostrar el mini reproductor (Task 6). *Salir* → guarda, `Dispose` del ícono y del atajo, cierra de verdad.
- [ ] Verificar con UIA: cerrar con el botón "Cerrar" deja el proceso vivo y la ventana oculta; el ícono existe (`Shell_TrayWnd` no se puede leer por UIA de forma fiable: verificar que `TrayIcon.Visible` y que *Salir* termina el proceso). Commit `Deja la app en la bandeja al cerrar`.

### Task 5: Atajo global Ctrl+Alt+L

**Files:** Create `src/LectorMd.App/Services/GlobalHotkey.cs`, modify `MainWindow.xaml.cs`.

**Interfaces:**
- Produces: `internal sealed class GlobalHotkey : IDisposable` con `static GlobalHotkey? TryRegister(Window window, ModifierKeys modifiers, Key key, Action pressed)` (null si falla).
- `MainWindow.ReadClipboardAloudAsync()`: misma lectura del portapapeles que `PasteAsync`, pero siempre en pestaña nueva (o la vacía), y empieza a leer desde el principio; si la ventana está oculta o minimizada no la muestra y abre el mini reproductor.

- [ ] Registrar en `OnLoaded` (con `EnsureHandle`); aviso si falla.
- [ ] Verificar: con la ventana oculta, poner texto en el portapapeles y simular la combinación con `SendInput` → aparece una pestaña pegada nueva y el estado pasa a "Pausar" (leyendo). Detener enseguida para no dejar audio sonando. Commit `Agrega el atajo global Ctrl+Alt+L para leer el portapapeles`.

### Task 6: Mini reproductor

**Files:** Create `src/LectorMd.App/MiniPlayerWindow.xaml(.cs)`, modify `MainWindow.xaml.cs`.

**Interfaces:**
- Produces: `MiniPlayerWindow` con los mismos métodos de presentación que usa la ventana con el `PlayerBar` que aplican aquí: `SetPosition(string section, string detail)`, `SetProgress(TimeSpan elapsed, TimeSpan total)`, `SetState(PlaybackState state, bool hasDocument)`, `SetSpeed(double)`, `ShowNotice(string)`; eventos `PlayPauseClicked`, `StopClicked`, `PreviousClicked`, `NextClicked`, `SpeedClicked` (pasa al siguiente paso de `PlaybackSpeeds.Steps`), `OpenMainClicked`.
- Diseño: tarjeta con los colores `Lector*`, esquinas 8, sombra; fila 1: ícono de la app + sección (SemiBold) + botón abrir (`E8A7`) + cerrar (`E711`); fila 2: detalle; fila 3: barra de progreso de 4 px + tiempos; fila 4: anterior, reproducir/pausar (acento), siguiente, detener, velocidad.

- [ ] La ventana principal crea/actualiza el mini reproductor desde `OnSegmentChanged`, `OnProgressChanged`, `OnPlaybackStateChanged`, `OnSpeedChanged`, `ShowStatus`; lo cierra en `Activated`/`StateChanged` a Normal.
- [ ] Autocierre según Global Constraints (`DispatcherTimer` de 10 s; `MouseEnter` lo detiene).
- [ ] Verificar con captura y UIA: aparece con la ventana oculta al leer; pausar → sigue 10 s y se cierra; "Abrir" muestra la ventana y cierra el mini. Commit `Agrega el mini reproductor flotante`.

### Task 7: Documentación y verificación final

- [ ] README (Uso: bandeja, Ctrl+Alt+L, mini reproductor, estado de Azure) y AGENTS.md (instancia única, `GlobalHotkey`, `TrayIcon`).
- [ ] `dotnet test` verde y `dotnet build -c Release` sin errores; commit.
