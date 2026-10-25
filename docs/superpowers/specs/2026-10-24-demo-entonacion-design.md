# Demo de entonación dentro de Shoken — Diseño

Fecha: 2026-10-24. Estado: aprobado en conversación; pendiente de revisión del documento.

Las decisiones del usuario van sin marca. Las que tomó Claude y el usuario todavía no revisó van marcadas
**(decisión de Claude)**.

## Para qué

La entonación es lo que distingue a Shoken (el nombre en la Store es «Shoken: lee Markdown con entonación»), pero no se
nota hasta que uno compara. El usuario quiere que la app traiga **una demo y una explicación resumida** para que
cualquiera pueda **apreciar y entender** cómo entona Shoken: oír el mismo texto con y sin la entonación, y ver qué hace
Shoken en cada parte.

La referencia es la demo que ya escuchó y aprobó (artifact «Cómo entona Shoken»): el mismo Markdown leído por Azure
(Dalia) dos veces, un selector para cambiar de versión en el mismo segmento, dos líneas de tiempo a escala y el
documento con una nota por parte. Esta versión vive dentro de la app, lee un texto nuevo sobre Shoken y suma la opción
de oírla con la voz del usuario.

Un éxito: alguien que acaba de instalar Shoken, sin clave de Azure ni internet, pulsa «Escucha cómo entona» en la
bienvenida y en menos de dos minutos oyó la diferencia, cambió de versión a mitad de una lista y entendió por qué suena
distinto. Quien ya usa Shoken con Kokoro puede pulsar «Tu voz» y oír lo mismo con su motor, sabiendo antes qué va a
cambiar y qué no.

## Objetivos

1. Una pestaña «Así entona Shoken» que se abre desde la bienvenida y desde Configuración.
2. Dos lecturas del mismo texto, **Con Shoken** y **Sin entonación**, con un selector que cambia de versión en el mismo
   segmento, sin cortar la reproducción.
3. Que se entienda **qué hace Shoken en cada parte**: pausas, punto final agregado, tono de títulos y negritas, textos
   que agrega (como «Consejo»).
4. Funciona para todos: con la grabación incluida (sin clave, sin internet, sin cuota).
5. **Tu voz**: generar las dos lecturas con el motor configurado del usuario, avisando el costo antes de gastar cuota.

## Fuera de alcance

- Un recorrido por todos los elementos de Markdown (tablas, código, Mermaid). Eso está en el README y en
  `docs/ejemplo.md`.
- Editar el texto de la demo desde la app o hacer la demo con un documento propio.
- Un modo «con / sin entonación» para cualquier documento.
- Cambiar cómo suena la lectura normal.
- Mostrar el SSML, el desglose del costo por etiqueta o «cómo se generó» (el usuario eligió «lo esencial»).

## Lo que haría fracasar esto aunque funcione

- **Que cambie el audio de la lectura normal.** Nada de esto puede alterar lo que `MarkdownSpeechParser`,
  `SpeechTextNormalizer`, `ChunkPlanner` o `SsmlBuilder` producen para un documento normal: cambiaría la clave de
  caché y se volvería a gastar cuota (ver `AGENTS.md`).
- **Gastar cuota de Azure o Google sin que el usuario lo sepa.** La grabación incluida no gasta nada; «Tu voz» con un
  motor de nube gasta solo después de que el usuario confirma en la barra.
- **Comparar dos voces distintas.** Si «Con Shoken» y «Sin entonación» salen de motores o voces diferentes, la
  comparación engaña. Antes que eso, error y reintentar.
- **Que la demo estorbe la lectura normal**: que suenen dos audios a la vez, que la barra espaciadora arranque la
  lectura de otra pestaña o que el reproductor de abajo quede en un estado raro.
- **Debilitar la CSP** más de lo necesario: solo se permite reproducir audio de `https://app.lector`.

## El texto de la demo

Aprobado tal cual. Cada parte explica lo que Shoken hace con ella, en el momento en que se oye; por eso la explicación
resumida es el propio texto y no una lista aparte (**decisión de Claude**, aprobada junto con el boceto).

```markdown
# Así lee Shoken

Shoken no lee el Markdown de corrido. Usa el **formato** del documento para decidir dónde hacer una pausa y cómo entonar cada parte.

## Los títulos

Un título suena un poco más lento y más grave. Aunque no lleve punto, Shoken se lo agrega para que la voz cierre la frase.

## Las listas

- Una pausa corta entre elementos
- Cada uno se cierra como una frase
- Y ninguno se pega al título

## Los pasos

1. Se lee el número de cada paso
2. Y hay una pausa antes del siguiente

> Una cita se distingue por la pausa al entrar y al salir.

> [!TIP]
> Las alertas se anuncian por su nombre: esta se lee «Consejo». Y **las negritas** suenan un poco más lentas y agudas.

---

Una línea horizontal marca un cambio de tema, con la pausa más larga.
```

Si al grabarlo alguna frase suena mal o el texto no dice la verdad sobre lo que hace Shoken (por ejemplo, si la cita no
lleva pausa al salir), se corrige el texto y no el comportamiento de Shoken.

«Usa el formato» se manda a la voz como «u sa el formato»: es la entrada `"Usa": "u sa"` del diccionario por omisión,
puesta a propósito para que Azure no lo lea como la sigla USA. No es un error.

## Cómo se ve y se usa

Boceto aprobado: `.superpowers/brainstorm/…/content/demo-en-la-app.html` (en el scratchpad de la sesión; no está en el
repo). Se resume aquí.

### Cómo se llega

- **Bienvenida:** un botón «Escucha cómo entona» junto a «Abrir archivo…».
- **Configuración:** un botón «Escuchar la demo de entonación» en la sección **Lectura** **(decisión de Claude:** el
  boceto decía «junto a la elección de voz», pero cada motor tiene su propia voz y «Lectura» es la sección común**)**.
  Al pulsarlo, Configuración guarda como con «Guardar» (para no perder lo cambiado) y se abre la pestaña; si no se
  guarda (por ejemplo, porque se canceló el aviso de Piper), no se abre **(decisión de Claude)**.
- Si la pestaña ya está abierta, se activa en vez de abrir otra **(decisión de Claude)**.

### La pestaña

- Nombre «Así entona Shoken», con un ícono de bocina de Segoe Fluent para distinguirla de los documentos.
- No se guarda en la sesión: al volver a abrir la app no reaparece **(decisión de Claude)**.
- El reproductor de abajo (`PlayerBar`) queda vacío, como en la bienvenida; la demo trae su propio reproductor. El
  índice, la búsqueda, los apuntes y recargar no aplican en esta pestaña.
- Toma los colores del tema (las variables de `lector.css`, también al cambiar de tema con la pestaña abierta). El
  tamaño del texto es el de las páginas de documento: no sigue `TextSize` (eso es solo de WPF); se agranda con el zoom
  del visor, como un documento.

### La página, de arriba abajo

1. **Título y entrada:** «Así entona Shoken» y dos o tres renglones: el mismo texto, la misma voz, dos lecturas;
   cambia de versión mientras suena y la otra sigue en la misma parte.
2. **Barra de la demo:** reproducir/pausa · selector **Con Shoken | Sin entonación** · tiempo (`0:21 / 1:08`) ·
   velocidad **1× | 1.25×** · selector **Dalia (grabada) | Tu voz · ‹motor›**. El usuario eligió que «Tu voz» vaya en
   esta barra. La velocidad arranca en la que el usuario tiene en la app si es una de las dos; si no, en 1×
   **(decisión de Claude)**.
3. **Dos líneas de tiempo a la misma escala:** una franja por segmento (los títulos más marcados), las pausas que pide
   Shoken rayadas, cursor de reproducción en la versión activa, el segmento actual resaltado en ambas. Clic en una
   franja: esa versión desde ese segmento.
4. **El documento con notas:** el Markdown como se ve en Shoken; a la derecha (debajo, en ventanas angostas), lo que
   hace Shoken en esa parte: `pausa 1.27 s`, `título: más lento y grave`, `+ punto`, `negrita: más aguda`,
   `dice «Consejo.»`. El segmento que suena se resalta como en la lectura normal. Clic o Enter en una parte: se oye
   desde ahí en la versión elegida.
5. **Una línea de costo:** Azure cobra el SSML, cuánto suma la entonación sobre el texto (en la demo de café de olla
   fue 76 %; la cifra sale de los datos de la grabación) y que lo que ya sonó queda en caché.

Teclado en esta pestaña: barra espaciadora reproduce o pausa la demo; ← y → saltan al segmento anterior o siguiente
**(decisión de Claude)**; Esc detiene. Ninguna de estas teclas debe llegar a la lectura normal.

### Las notas salen de los datos

Las notas no se escriben a mano: se calculan del `SpeechDocument` del texto de la demo con las mismas reglas de la app
(tipo de segmento, `Pauses.Gap` y la pausa inicial de cada bloque, tramos en negrita, valores de `ProsodyProfile`, si
`EnsureTerminalPunctuation` agregó el punto, texto que puso el parser, como «Consejo.»). Así, si cambia una regla, la
nota no miente **(decisión de Claude)**. La lógica vive en `Shoken.Core` y tiene pruebas.

Con Kokoro o Piper en «Tu voz», las notas de tono se muestran atenuadas y la barra explica, en una línea, que esos
motores reciben las pausas y el punto final pero no el cambio de tono **(decisión de Claude)**.

### Convivencia con la lectura normal **(decisiones de Claude)**

- Reproducir la demo pausa la lectura normal si estaba sonando.
- Salir de la pestaña de la demo (cambiar de pestaña, minimizar a la bandeja) pausa la demo.
- `Ctrl+Alt+L` y el mini reproductor siguen controlando solo la lectura normal.

## El audio

### Grabado (incluido en la app)

- Voz `es-MX-DaliaNeural` de Azure, velocidad 0, **diccionario de pronunciación por defecto**
  (`PronunciationDictionary.Defaults`, no el del usuario), opciones del parser por defecto. MP3 24 kHz 96 kbps, como
  el motor de la app.
- **Con Shoken** es la lectura normal: `ChunkPlanner` + `SsmlBuilder` + Azure, bloque por bloque, unidos en un solo
  archivo con las marcas de cada segmento desplazadas.
- **Sin entonación** es un solo SSML con el texto de cada segmento, normalizado igual, unido con espacios: sin
  `<break>`, sin `<prosody>` y sin el punto que agrega Shoken. Lleva un `<bookmark>` por segmento, que no suena, para
  tener marcas exactas.
- Se incluyen dos MP3 (unos 1.3 MB) y un archivo de datos con las marcas, las duraciones, la voz, los caracteres
  facturables y la huella del SSML de cada bloque.
- Los genera un script del repo, `empaquetado/generar-demo.cs`, junto a `generar-icono.cs` **(decisión de Claude)**.
  Usa `Shoken.Core` sin cambios, toma la clave de Azure de la configuración del desarrollador (DPAPI, nunca la
  imprime) y suma lo gastado al contador del mes. Cada grabación gasta unos 2 500 caracteres.
- **Una prueba vigila que la grabación no quede vieja:** arma el SSML del texto de la demo como lo haría hoy la app
  (diccionario por defecto, Dalia, velocidad 0) y compara sus huellas con las del archivo de datos. Si no coinciden,
  falla con un mensaje que dice cómo volver a grabar. Lo mismo si cambia el texto de la demo.

### Tu voz

- Usa los servicios de la app con la configuración actual (motor, voz, velocidad, diccionario del usuario) y la cadena
  de siempre (`HybridTtsEngine`): misma caché, mismo contador de cuota, mismos límites de peticiones y la opción «solo
  motores locales».
- **Con Shoken** es la lectura normal del texto de la demo, bloque por bloque.
- **Sin entonación** es un solo bloque con un solo segmento de tipo párrafo, sin estilos ni pausas, con el texto de
  todos los segmentos unido con espacios. Pasa por la misma interfaz `ITtsEngine`, así que funciona igual con Azure,
  Google, Kokoro y Piper, sin tocar los motores **(decisión de Claude)**. Va con el texto sin normalizar, porque el
  motor lo normaliza; normalizar dos veces podría transformar algo de más. Como es un solo segmento, en esta versión
  el resaltado y el cursor por segmento se estiman en proporción a los caracteres de cada uno.
- **Antes de generar:** el selector dice qué motor y qué voz se van a usar. Si es de nube y algo no está en caché, la
  barra muestra «Gasta unos N caracteres de Azure (te quedan M) · Generar · Cancelar». N es el costo real de lo que
  falta, calculado con el mismo SSML que se va a mandar. Con motores locales o si todo está en caché, genera sin
  preguntar.
- **Mientras genera:** «Generando 2 de 4…» en la barra; la versión grabada sigue disponible. Volver a «Dalia» o cerrar
  la pestaña cancela lo que no se pidió (lo ya pedido a Azure termina y queda en caché, como en la lectura normal).
- **Una sola voz:** si los bloques no salen todos del mismo motor y voz (`AudioChunk.EngineName`), no se compara;
  la barra dice qué pasó y ofrece «Reintentar».
- **Sin motor configurado:** «Tu voz» aparece desactivado y explica que hay que configurar una voz en Configuración.
- **Errores** (sin conexión, clave inválida, cuota agotada): el mensaje del motor en la barra y «Reintentar».
- El audio generado se copia a la carpeta que sirve `app.lector` para que la página lo reproduzca (MP3 unidos; WAV
  unidos con `WavAudio.Concatenate`). Se genera una vez por sesión; la segunda vez sale de la caché.

## Restricciones técnicas

- **CSP:** `media-src` agrega `https://app.lector`. No cambia nada más; el script de la demo se sirve desde
  `app.lector` como `lector.js`.
- Los recursos nuevos (MP3, datos, script, estilos) van como recursos incrustados que `DocumentView.WriteAssets`
  escribe en la carpeta de la vista, igual que `lector.css`.
- Los mensajes de la página de la demo hacia la app solo se aceptan de la página actual (`IsCurrentPage`).
- `Shoken.Core` no referencia WPF: la lógica de la demo (notas, texto plano, plan, datos de la grabación) va en Core;
  la pestaña, la página y la generación con la app van en `Shoken.App`.
- Textos de la interfaz con `LectorFontSize*`, no tamaños fijos. Popups y foco: ver `AGENTS.md`.
- Clases `sealed`, comentarios y nombres de prueba en español (`Componente_Comportamiento`).
- Verificar que los términos de Azure AI Speech permiten distribuir en la app el audio de una voz neuronal
  prediseñada. Si no, la grabación incluida se hace con Kokoro (Apache 2.0) y se pierde la prosodia en la versión
  grabada. Esto se le consulta al usuario antes de cambiar de voz.

## Documentación

- `README.md`: una sección corta sobre la demo (dónde está, qué compara, que «Tu voz» puede gastar cuota). La tabla
  «Cómo se lee cada elemento» no cambia, porque la lectura no cambia.
- `AGENTS.md`: la grabación de la demo, cómo regenerarla y la prueba que lo exige; la regla de `media-src`.
- `docs/demo-shoken.md` (la guía del audio de demostración anterior) se queda como está **(decisión de Claude)**.

## Cómo se prueba

- `dotnet test` en verde, con pruebas nuevas para las notas, el texto plano, el plan de «Tu voz» y la huella de la
  grabación.
- A mano, en la app real, manejando la página por DevTools (puerto de depuración de WebView2 y CDP desde node). **No se
  simula ratón ni teclado**: el usuario trabaja en el mismo escritorio. Al terminar, la app se reinicia sin el puerto.
- Casos: bienvenida → demo; Configuración → demo; cambiar de versión a mitad de un segmento; velocidad; tema claro y
  oscuro y cambio de tema en vivo; ventana angosta; «Tu voz» con Kokoro (sin costo), con Azure (aviso y
  confirmación), sin motor, sin conexión; la lectura normal de otra pestaña no se ve afectada.

## Lo que queda al constructor

- Nombres de clases, archivos y mensajes entre la página y la app.
- Dónde viven el texto de la demo y el archivo de datos, mientras la prueba de la huella pueda leerlos.
- El formato del archivo de datos.
- Si el script de la página es un archivo aparte o un módulo de `lector.js`.
- Los textos finales de la interfaz, en el tono del resto de la app.
