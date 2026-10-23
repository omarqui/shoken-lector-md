# Lector MD

App de Windows (WPF, .NET 10) que lee archivos Markdown en voz alta en **español de México** y aprovecha el formato del documento para decidir las **pausas y la entonación**. Mientras lee, resalta el párrafo y lo va siguiendo en pantalla.

- **Motor principal:** Azure Speech, voces neuronales `es-MX`, con la capa gratuita **F0** (500 000 caracteres al mes).
- **Respaldo sin conexión:** Piper, una voz neuronal local y gratuita. Entra solo cuando se acaba la cuota, no hay internet o Azure falla.
- **Costo:** $0. El audio generado se guarda en caché, así que volver a escuchar un documento no gasta cuota.

---

## Cómo se lee cada elemento del Markdown

| Markdown | Cómo suena |
|---|---|
| `# Encabezado` | Pausa antes (1.1 s en h1 y 0.55 s en h4–h6), un poco más lento y grave, y cierre con punto para que la entonación baje |
| Párrafo | Pausa de ~0.3 s al terminar. Los párrafos muy largos se parten por oraciones |
| `**negrita**` | Un poco más lenta y aguda (velocidad −4 %, tono +6 %) |
| `1.` `2.` listas numeradas | Se lee "uno…, dos…", con pausa corta entre elementos |
| `- [x]` / `- [ ]` | "Hecho: …" / "Pendiente: …" |
| `> cita` | Pausas al entrar y al salir |
| `> [!WARNING]` (alertas de GitHub) | "Advertencia." y luego el contenido. También Nota, Consejo, Importante y Precaución |
| Tablas | "Tabla con columnas: Motor, Costo." Luego, por cada fila: "Motor: Azure. Costo: Gratis." |
| Bloques de código | "Bloque de código en C#." (en Configuración puedes omitirlos o leerlos) |
| Diagramas ` ```mermaid ` | Se dibujan (secuencia, flujo, clases, estados, Gantt…) con los colores del tema y se leen como "Diagrama." |
| `---` | Pausa extra, como cambio de tema |
| Enlaces | Solo se lee el texto. Las URL sueltas se leen como "enlace" |
| Imágenes, HTML y front matter YAML | Se omiten |
| ✅ / ❌ | "sí" / "no". Los demás emojis se omiten |
| `→`, `~5`, `p. ej.`, `&` | ", ", "aproximadamente 5", "por ejemplo", "y" |

La *cursiva* y las citas no cambian la prosodia porque Azure también cobra las etiquetas SSML. Se distinguen con pausas, que cuestan menos y se notan más. Si quieres activarlas, ajusta `ProsodyProfile` en `src/LectorMd.Core/Ssml/SsmlBuilder.cs`.

## Requisitos

- Windows 10 u 11 de 64 bits
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Microsoft Edge WebView2 Runtime (ya viene en Windows 11 y en Windows 10 actualizado)

## Compilar y ejecutar

```powershell
dotnet build -c Release
dotnet test
dotnet run --project src/LectorMd.App -c Release
```

Para tener un ejecutable que puedas fijar en la barra de tareas o usar con "Abrir con…":

```powershell
dotnet publish src/LectorMd.App -c Release -r win-x64 --self-contained false -o publicar
# publicar\LectorMd.exe  (acepta la ruta de un .md como argumento)
```

## 1. Configurar Azure (motor principal, gratis)

1. Entra a [portal.azure.com](https://portal.azure.com), elige **Crear un recurso** y busca **Speech** (Azure AI Speech).
2. En **Plan de tarifa** elige **Free F0**. En la región, elige una cercana, por ejemplo *East US* o *South Central US*.
3. Cuando se cree, ve a **Claves y punto de conexión** y copia la **Clave 1** y la **Ubicación/Región** (por ejemplo `eastus`).
4. En la app, abre ⚙ **Configuración**, pega la clave y la región, elige la voz y pulsa **Probar Azure**.

Lo que conviene saber de la capa F0:

- Incluye 500 000 caracteres al mes y hasta 20 peticiones por minuto. La app agrupa los párrafos en bloques de unos 700 caracteres y adelanta solo 2, así que no llega a ese límite.
- **Las etiquetas también cuentan.** Azure cobra todo el SSML excepto `<speak>` y `<voice>`: cada pausa, cada marca de resaltado y cada negrita. En documentos con párrafos normales, el total es aproximadamente 1.5 veces el texto. Con muchas listas o tablas cortas puede llegar a 2.5 veces.
- La app lleva un **contador local**. Abajo a la derecha del reproductor, el estado de Azure muestra un punto (verde: funciona; ámbar: respaldo; rojo: sin motor), una barra corta y el porcentaje del mes; la barra realza los consumos bajos para que se noten, y el porcentaje es el exacto. Al pasar el mouse se abre un panel con el detalle (voz, si el audio sale de la caché, caracteres usados, fecha de reinicio y última consulta a Azure). El límite por defecto es de 480 000 caracteres para dejar margen; al llegar, cambia a Piper. El contador se reinicia el día 1 de cada mes (UTC).
- **Consumo real:** el botón *Consultar consumo real en Azure* del panel consulta en Azure Monitor la métrica *Synthesized Characters* del mes. Si Azure reporta más que el contador local (por ejemplo, porque usas la misma clave en otras apps), el contador sube a esa cifra; nunca baja. La primera vez se abre el navegador para que inicies sesión con la cuenta que tiene el recurso. Después la sesión queda guardada en la caché cifrada de Windows. Si la cuenta tiene varios recursos de Speech, la app te pide elegir uno y lo recuerda. Azure tarda de 1 a 3 minutos en reflejar lo más reciente. Si tu cuenta es personal (Microsoft) y no encuentra el recurso, escribe el **Id de directorio** en Configuración: está en el portal, en *Microsoft Entra ID → Información general*.
- La clave se guarda cifrada con DPAPI: solo tu usuario de Windows puede leerla.

Voces es-MX: **Dalia** (mujer) y **Jorge** (hombre) son las más naturales. También están Beatriz, Candela, Carlota, Cecilio, Gerardo, Larissa, Liberto, Luciano, Marina, Nuria, Pelayo, Renata y Yago. El campo es editable, así que puedes escribir cualquier otra voz de Azure.

## 1b. Configurar Google Cloud (respaldo de Azure, opcional)

Google entra cuando Azure no está disponible y antes de Piper. Sus voces **Neural2** en español latino (`es-US-Neural2-A` mujer, `B` y `C` hombres) tienen **1 000 000 de caracteres gratis al mes** y marcas de tiempo, así que el resaltado funciona igual que con Azure.

1. Entra a [console.cloud.google.com](https://console.cloud.google.com) y crea un proyecto (o usa uno).
2. En **Facturación**, vincula una cuenta de facturación. Google la exige aunque solo uses lo gratuito. Recomendado: en *Presupuestos y alertas* crea un presupuesto de 1 USD con aviso por correo.
3. En **APIs y servicios → Biblioteca**, busca **Cloud Text-to-Speech API** y pulsa **Habilitar**.
4. En **APIs y servicios → Credenciales**, elige **Crear credenciales → Clave de API**. Después edita la clave y, en *Restricciones de API*, elige **Restringir clave → Cloud Text-to-Speech API**. Así, si alguien la obtiene, no la puede usar para otra cosa.
5. En la app, abre ⚙ **Configuración → Google Cloud**, pega la clave, elige la voz y pulsa **Probar Google**.

Al pasar el plan gratuito Google **cobra** en lugar de cortar, así que la app lleva su propio contador (`uso-google.json`) y no usa Google más allá del **límite mensual** (950 000 por defecto). En el panel del estado (al pasar el mouse) está el consumo de Google y el selector **Usar: Automático · Azure · Google · Piper**, para probar cada voz por separado.

## 2. Configurar Piper (respaldo sin conexión)

1. Descarga **[piper_windows_amd64.zip](https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip)** y descomprímelo, por ejemplo en `C:\Herramientas\piper\`. Deja juntos `piper.exe`, sus DLL y la carpeta `espeak-ng-data`.
2. Descarga una voz es_MX de [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices/tree/main/es/es_MX). Necesitas los **dos** archivos y deben quedar en la misma carpeta:
   - `es/es_MX/claude/high/es_MX-claude-high.onnx` y `.onnx.json`: voz de mujer, combina con Dalia.
   - `es/es_MX/ald/medium/es_MX-ald-medium.onnx` y `.onnx.json`: voz de hombre, combina con Jorge.
3. En ⚙ **Configuración**, indica `piper.exe` y el `.onnx`, y pulsa **Probar Piper**.

Si marcas **Usar solo Piper**, la app nunca se conecta a Azure.

## Cómo decide qué motor usar

Para cada bloque de audio, en este orden:

1. **Caché de Azure.** Si ese texto ya se generó antes, se reproduce sin gastar cuota, aunque no haya internet.
2. **Azure**, si hay clave, queda cuota y responde.
3. **Piper**, en cualquier otro caso:

| Situación | Qué hace |
|---|---|
| Se alcanzó el límite mensual configurado | Usa Piper hasta el día 1 o hasta que subas el límite |
| Azure responde que la cuota se agotó | Usa Piper el resto del mes |
| Sin conexión o tiempo de espera agotado | Usa Piper y reintenta Azure en 45 s |
| Demasiadas peticiones (429) | Reintenta 2 veces y luego usa Piper durante 60 s |
| Clave o región inválida | Usa Piper hasta que corrijas la configuración |

Mientras se usa el respaldo, el punto del estado (en el reproductor) se pone **ámbar** y el motivo aparece en su panel al pasar el mouse.

## Uso

| Acción | Atajo |
|---|---|
| Leer / pausar | `Espacio` o el botón ▶ |
| Leer desde un párrafo | **Doble clic** sobre él |
| Sección siguiente / anterior | `Ctrl` + `→` / `Ctrl` + `←` |
| Detener | `Esc` |
| Abrir (uno o varios) | `Ctrl` + `O`, o arrastra los `.md` a la ventana |
| Pegar una ruta o un texto Markdown | `Ctrl` + `V` |
| Pestaña nueva / cerrar pestaña | `Ctrl` + `T` / `Ctrl` + `W` (o clic central en la pestaña) |
| Pestaña siguiente / anterior | `Ctrl` + `Tab` / `Ctrl` + `Shift` + `Tab` |
| Mostrar u ocultar el índice | `Ctrl` + `B` |
| Buscar en el documento | `Ctrl` + `F`, o la lupa de la barra de comandos |
| Recargar (y releer el diccionario) | `F5` |
| Configuración | `Ctrl` + `,` |
| Leer lo copiado, desde cualquier app | `Ctrl` + `Alt` + `L` |
| Retroceder / adelantar 10 segundos | `←` / `→`, o los botones −10 y +10 del reproductor |

- **Pestañas:** viven en la barra de título (con muchas, se recorren con la rueda del mouse o Ctrl+Tab). Cada documento se abre en su pestaña, con un ícono que distingue los archivos `.md` (al pasar el mouse muestran su ruta) del texto pegado. Si ya estaba abierto, la app va a esa pestaña. Al cambiar de pestaña la lectura se pausa, y cada una recuerda su párrafo y su posición. Al cerrar la app se guardan las pestañas abiertas y se reabren al iniciar.
- **Pegar (`Ctrl` + `V`):** si el portapapeles tiene rutas (una por línea, con o sin comillas, o archivos copiados en el Explorador), se abren. Si tiene otro texto, se lee como Markdown en una pestaña nueva, o en la vacía si estás en una. El texto pegado se guarda hasta que cierras su pestaña.
- **Índice:** el panel izquierdo muestra los títulos (h1 a h3) con la duración de cada sección (con ≈ mientras es estimada). Con un clic vas al título; para leer desde ahí usa su ▶ (aparece al pasar el mouse y en la sección actual) o `Enter`. Mientras lee se marca la sección actual.
- **Contraer secciones:** la flecha de un título oculta sus subtítulos (o `←` / `→` con la fila elegida), y el botón del encabezado contrae o expande todo. Una fila contraída muestra la duración y los resultados de búsqueda de toda su sección, y se marca si lo que se lee está adentro. Cada pestaña recuerda lo contraído mientras la app sigue abierta.
- **Buscar (`Ctrl` + `F`):** abre una barra arriba a la derecha del documento. No distingue acentos ni mayúsculas (`configuracion` encuentra «Configuración»), resalta todos los resultados y empieza por el primero visible. `Enter` / `Shift` + `Enter` (o `F3` / `Shift` + `F3`) van al siguiente o al anterior, `Ctrl` + `Enter` (o ▶) lee desde el párrafo del resultado y `Esc` cierra la barra sin detener la lectura. Mientras buscas, el índice muestra cuántos resultados hay en cada sección. Al reabrirla vuelve la búsqueda anterior.
- **Reproductor (abajo):** la línea de tiempo está dividida por capítulos (los títulos del índice); un clic en un tramo lee desde ahí. Debajo, como en Spotify: la sección y el párrafo a la izquierda, los controles al centro, y la velocidad y el estado de Azure a la derecha. Las duraciones de lo que todavía no se generó son estimadas (≈) y se corrigen cuando se genera su audio.
- **Velocidad** (selector del reproductor, de 0.75× a 1.75×): acelera o frena la reproducción al instante, sin volver a generar audio ni gastar cuota.
- **Tema:** el botón sol/luna alterna claro y oscuro (la primera vez sigue el tema de Windows). El documento cambia de colores sin recargarse.
- **Tamaño del texto** (Configuración › Interfaz): Normal, Grande (+15 %, el predeterminado) o Muy grande (+30 %) para pestañas, índice, reproductor, paneles y mini reproductor. Se ve al instante; el índice se ensancha en la misma proporción. El documento se agranda aparte, con `Ctrl` + rueda del mouse.
- **Bandeja del sistema:** cerrar la ventana no cierra la app: queda en la bandeja (doble clic la abre; clic derecho: *Abrir*, *Leer portapapeles*, *Salir*). Hay una sola instancia: abrir un `.md` desde el Explorador con la app abierta lo agrega como pestaña.
- **Leer lo copiado (`Ctrl` + `Alt` + `L`):** desde cualquier app, lee lo que hay en el portapapeles (rutas a `.md` o texto Markdown). El texto queda como pestaña pegada, así puedes retomarlo y su audio queda en caché. Si otra app ya usa la combinación, se avisa al iniciar.
- **Mini reproductor:** si la ventana está oculta o minimizada mientras suena algo (o detrás de otra app cuando lees con `Ctrl` + `Alt` + `L`), aparece abajo a la derecha con la sección, el avance, los controles y la velocidad. No roba el foco y se cierra solo tras 10 segundos en pausa o detenido (con el mouse encima espera).
- **Velocidad de la voz** (Configuración): cambia la síntesis misma. Como el texto enviado cambia, el audio se vuelve a generar.
- Los enlaces a otros `.md` de la misma carpeta se abren en la app, en una pestaña nueva. Los enlaces web se abren en tu navegador.

## Diccionario de pronunciación

`%LOCALAPPDATA%\LectorMd\pronunciacion.json` reemplaza **palabras completas**, sin distinguir mayúsculas, antes de leer. Así se evita que la voz mexicana lea mal siglas o términos en inglés:

```json
{
  "JSON": "yéison",
  "C#": "si sharp",
  ".NET": "dot net",
  "Docker": "dóker",
  "endpoint": "éndpoint"
}
```

Se crea con unas 200 entradas iniciales (siglas, herramientas y términos de programación en inglés). Ábrelo desde Configuración, edítalo y pulsa `F5`.

## Datos que guarda

Todo vive en `%LOCALAPPDATA%\LectorMd\`:

| Archivo | Contenido |
|---|---|
| `settings.json` | Configuración y pestañas abiertas. La clave de Azure va cifrada |
| `uso-azure.json` | Caracteres enviados a Azure este mes |
| `pegados\` | Texto Markdown pegado en pestañas. Cada archivo se borra al cerrar su pestaña |
| `cuenta-azure.json` | Qué cuenta de Azure se usa para consultar el consumo real (sin contraseñas ni tokens) |
| `pronunciacion.json` | Diccionario de pronunciación |
| `cache\` | Audio generado (MP3 de Azure, WAV de Piper). Se recorta solo al pasar de 1 GB |
| `errores.log` | Detalle de errores inesperados |

## Estructura del código

```
src/LectorMd.Core          Lógica sin dependencias de interfaz (net10.0)
  Parsing/                 Markdown → segmentos (Markdig) + HTML con data-seg
  Text/                    Normalización para voz y diccionario de pronunciación
  Ssml/                    SSML con <bookmark> por segmento, pausas y prosodia
  Planning/                Agrupa segmentos en bloques de audio deterministas (para la caché)
  Tts/                     Azure (Speech SDK), Piper (proceso persistente --json-input) e híbrido
  Caching/, Usage/         Caché de audio, contador mensual de cuota y consumo real (Azure Monitor)
src/LectorMd.App           WPF: WebView2 para el documento, MediaPlayer para el audio
  Controls/                Barra de título (botones de ventana) y reproductor con línea de tiempo
tests/LectorMd.Core.Tests  Pruebas unitarias xUnit del núcleo
docs/ejemplo.md            Documento de prueba con todos los elementos
docs/demo-lector.md        Guía rápida (la del audio de demostración)
```

El resaltado es exacto porque cada segmento lleva un `<bookmark>` en el SSML. Azure devuelve el instante en que empieza cada uno; con Piper, los instantes se calculan al unir los WAV.

## Límites conocidos

- El resaltado va por bloque (párrafo, elemento de lista o fila), no palabra por palabra.
- Las voces es-MX no son multilingües: para términos en inglés usa el diccionario.
- Piper no cambia la entonación con las negritas, solo respeta las pausas.
- La velocidad de la barra superior usa el `SpeedRatio` del reproductor de Windows. Si a alguna velocidad notas un cambio de tono, usa mejor la velocidad de la voz en Configuración.
