# Google Cloud Text-to-Speech como respaldo de Azure — Plan de implementación

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agregar Google Cloud TTS como segundo motor en la nube: caché → Azure → **Google** → Piper.

**Architecture:** `GoogleTtsEngine` (Core) llama a la API REST `v1beta1/text:synthesize` con clave de API y pide las marcas de tiempo (`enableTimePointing: SSML_MARK`) para el resaltado. El SSML es el mismo que arma `SsmlBuilder` para Azure, traducido por `GoogleSsml.FromAzure` (bookmark → mark, sin `<voice>`, prosodia en el formato de Google). `HybridTtsEngine` pasa de "Azure y Piper" a una lista ordenada de motores en la nube, cada uno con su contador de cuota y su estado de pausa por fallas, y Piper al final.

**Tech Stack:** .NET 10, `HttpClient` + `System.Text.Json`, xUnit con un `HttpMessageHandler` falso.

**Spec:** decisiones del usuario el 2026-10-23: Google entra **como respaldo de Azure, antes de Piper**; se conecta con **clave de API**; voz **Neural2 es-US** (por defecto `es-US-Neural2-A`, mujer; B y C hombres).

## Global Constraints

- El SSML y la clave de caché de **Azure** no cambian (cero cuota extra). Google usa su propia clave de caché: `ComputeKey("google", "mp3-24k", ssmlDeGoogle)`.
- Clave de Google cifrada con DPAPI como la de Azure (`StoredSettings.GoogleKeyProtected`); nunca en texto plano, logs ni pruebas reales.
- Contador propio `uso-google.json`; límite por defecto **950 000** caracteres al mes (el plan gratuito de Neural2 es 1 000 000). Se cuenta el largo del SSML enviado (estimación conservadora).
- El audio de Google en caché solo se reutiliza mientras Azure no esté disponible (igual que hoy con Piper), para no quedarse con la voz de respaldo.
- Textos en español; identificadores en inglés.

## Review Focus

- **Clave inválida o API no habilitada (403)**: Google queda deshabilitado con un motivo claro y la lectura sigue con Piper; no se reintenta en cada bloque.
- **SSML que Google rechaza (400)**: se informa como error del bloque y pasa a Piper, sin romper la lectura.
- **Azure vuelve después de usar Google**: el siguiente bloque usa Azure otra vez (no se queda con la voz de Google).
- **Sin Azure configurado, solo Google**: Google funciona como principal.
- **Google sin marcas en la respuesta**: el audio se reproduce; el resaltado cae al inicio del bloque (marcas completadas con 0).

---

### Task 1: SSML de Google

**Files:** Create `src/LectorMd.Core/Ssml/GoogleSsml.cs`; test `tests/LectorMd.Core.Tests/GoogleTtsTests.cs`.

**Interfaces:** `public static class GoogleSsml { public static string FromAzure(string azureSsml); }`

- [ ] Tests que fallan: `<bookmark mark="3"/>` → `<mark name="3"/>`; sin `<voice>` ni `xmlns`; `rate="-4%"` → `rate="96%"`; `pitch="+6%"` → `pitch="+1st"` (semitonos redondeados a 1 decimal: 12·log2(1.06) ≈ 1.0); `<break time="300ms"/>` intacto; texto con `&amp;` sigue escapado.
- [ ] Implementar con `XDocument`. Verde. Commit.

### Task 2: Motor de Google

**Files:** Create `src/LectorMd.Core/Tts/GoogleTtsEngine.cs`; tests en `GoogleTtsTests.cs`.

**Interfaces:** `public sealed record GoogleTtsOptions { string ApiKey; string Voice = "es-US-Neural2-A"; int RatePercent; }`; `public sealed class GoogleTtsEngine(GoogleTtsOptions options, SsmlBuilder ssml, AudioCache cache, UsageTracker usage, HttpClient http) : ITtsEngine`; `Name => "Google · {Voice}"`; `internal static TtsException MapError(HttpStatusCode status, string body)`.

- [ ] Tests que fallan (handler falso): el pedido va a `v1beta1/text:synthesize?key=…` con `input.ssml`, `voice.languageCode` = prefijo de la voz (`es-US`), `voice.name`, `audioConfig.audioEncoding = MP3`, `enableTimePointing = ["SSML_MARK"]`; la respuesta (`audioContent` base64 + `timepoints[{markName,timeSeconds}]`) queda en caché con sus marcas; `TryGetCached` lo devuelve después; suma uso; sin cuota local → `LocalLimitReached` sin llamar a la red; 403 → `Authentication`; 429 con "quota" → `QuotaExceeded`; 429 → `RateLimited`; 400 → `Other`; `HttpRequestException` → `Network`.
- [ ] Implementar. Verde. Commit.

### Task 3: Cadena caché → Azure → Google → Piper

**Files:** Modify `src/LectorMd.Core/Tts/HybridTtsEngine.cs`, `src/LectorMd.Core/EngineServices.cs`; test `tests/LectorMd.Core.Tests/HybridChainTests.cs`.

**Interfaces:** `public sealed record CloudEngine(ITtsEngine Engine, UsageTracker Usage, string Label)`; `HybridTtsEngine(IReadOnlyList<CloudEngine> clouds, ITtsEngine? local, bool offlineOnly, Func<DateTime>? utcNow = null)`.

- [ ] Tests que fallan con motores falsos: Azure ok → Azure; Azure falla de red → Google (estado "respaldo", motivo de Azure); Azure y Google sin cuota → Piper; Google 403 → no se vuelve a llamar a Google en el siguiente bloque; Azure se recupera tras su pausa → vuelve a Azure; solo Google configurado → Google sin "respaldo"; caché de Google solo si Azure no está disponible.
- [ ] Refactor del motor híbrido manteniendo el comportamiento actual de Azure y Piper. Verde. Commit.

### Task 4: Configuración y app

**Files:** `AppSettings` (`GoogleApiKey` [JsonIgnore], `GoogleVoice`, `GoogleMonthlyCharacterLimit`), `AppPaths.GoogleUsageFile`, `SettingsStore` (cifrado), `SettingsWindow` (sección Google: clave, voz editable con A/B/C, límite, "Probar Google"), panel de estado (línea de consumo de Google), README (cómo crear y restringir la clave).

- [ ] Implementar; `dotnet build` sin errores; verificar con UIA que la sección existe y que guardar cifra la clave (`settings.json` no contiene la clave en claro). Commit.
