# La palabra que suena y el tono de comillas y paréntesis — Diseño

Fecha: 2026-10-26. Estado: revisado por un constructor; pendiente de que el usuario apruebe el documento.

Las decisiones del usuario van sin marca. Las que tomó Claude y el usuario todavía no revisó van marcadas
**(decisión de Claude)**.

Son dos funciones independientes que se diseñan juntas porque las dos tocan cómo se lee un párrafo. Se pueden
construir y entregar por separado; ninguna depende de la otra.

---

## Parte A — Resaltar la palabra que suena

### Para qué

El usuario escucha mirando a otra cosa y, al volver a la pantalla, quiere encontrar de inmediato dónde va la lectura.
Hoy se resalta el párrafo entero; en un párrafo largo cuesta ubicarse. **No** es para leer siguiendo la voz palabra
por palabra: si el resaltado va una o dos palabras adelantado o atrasado, sigue cumpliendo.

Un éxito: con un párrafo de 8 líneas sonando, el usuario mira la pantalla y en menos de un segundo sabe en qué línea va.

### Objetivos

1. Dentro del párrafo resaltado, marcar la palabra que suena.
2. Que funcione con todos los motores: exacto con Azure cuando el audio es nuevo, estimado en los demás casos.
3. Un interruptor en Configuración para apagarlo, **encendido por omisión** (decisión de Claude, aceptada por el usuario).

### Fuera de alcance

- Cambiar el estilo o el color de la palabra desde Configuración (solo encendido/apagado).
- Activarlo o desactivarlo por motor.
- Resaltado por palabra en la pestaña de la demo (`demo.js`) y en el mini reproductor.
- Hacer clic en una palabra para corregir su pronunciación.

### Lo que no puede pasar (anti-objetivos)

- **Gastar cuota de Azure para conseguir tiempos por palabra.** Nunca se vuelve a sintetizar un bloque que ya está en
  caché solo porque le faltan los tiempos.
- **Cambiar la clave de caché o el SSML.** Los tiempos por palabra son datos adicionales de la entrada de caché, no
  parte de la clave.
- Marcar una palabra en otro párrafo distinto del que suena, o que el resaltado salte hacia atrás mientras avanza solo.
- Que la marca tape o pelee con el resaltado de la búsqueda (Ctrl+F).

### Cómo funciona

**De dónde salen los tiempos.**

- **Azure, audio nuevo:** el `SpeechSynthesizer` emite `WordBoundary` por cada palabra (`AudioOffset`, `Duration`,
  `Text`, `BoundaryType`). Se registran junto a los bookmarks en `AzureTtsEngine`, descartando los de tipo
  puntuación y oración. Cada palabra se asigna al segmento cuyo bookmark es el último anterior a su `AudioOffset`.
  El evento no agrega nada al SSML: no cuesta caracteres.
- **Todo lo demás** (Google, Kokoro, Piper, y el audio de Azure ya guardado sin tiempos): se **estiman**. La duración
  hablada de un segmento es desde su marca hasta la marca siguiente (o el final del audio) menos la pausa que lo
  precede en el bloque siguiente (`GapBefore`). Se reparte entre las palabras del texto normalizado del segmento en
  proporción a sus caracteres. La estimación vive en Core, con pruebas.
- Google tiene `<mark>` y podría dar tiempos exactos, pero cuesta un `<mark>` por palabra: **no** se usa.

**Dónde se guardan.** `AudioCache` guarda una lista opcional de palabras por entrada (segmento, inicio, texto) en el
mismo `.json` de la entrada. Las entradas sin la lista siguen siendo válidas; `AudioChunk` expone los tiempos
(exactos o estimados) de forma que la App no sepa de dónde vinieron.

**Emparejar lo que se dice con lo que se ve.** Las palabras habladas no siempre están escritas: números («3» → «tres»),
el diccionario de pronunciación, los textos que agrega la app («Código en C#:», «Hecho:», el encabezado antes de cada
celda de tabla, «Imagen: …»). El emparejamiento se hace **en la página** (`lector.js`), que es quien conoce el DOM:

- Al cambiar de segmento, la App manda a la página las palabras de ese segmento. Luego, al cambiar de palabra, manda
  solo el índice.
- La página pliega (sin acentos, minúsculas) las palabras del texto visible del bloque (`data-seg`) y las habladas, y
  las empareja en orden (secuencia común más larga, como ya hace el módulo de apuntes con `textIndex`).
- El emparejamiento avanza siempre hacia adelante dentro del bloque. Un bloque largo puede estar partido en varios
  segmentos (`SplitLong`) con el mismo `data-seg`; el segmento siguiente empieza a emparejar donde terminó el anterior.
- Si la palabra que suena no tiene pareja, queda marcada la última que sí la tuvo. Mientras suena un anuncio que no
  está escrito, no se marca ninguna palabra y solo queda el resaltado del párrafo **(decisión de Claude)**.

**Cómo se ve.** Se dibuja con `CSS.highlights` (nombre propio, por ejemplo `lector-word`), sin tocar el DOM, encima del
resaltado del párrafo y con un fondo más intenso que el de este **(decisión de Claude)**. El color sale de
`ThemePalettes` como los demás, en los cuatro temas claros y oscuros, y `ThemeTests` exige contraste AA del texto sobre
ese fondo. En pausa la palabra sigue marcada **(decisión de Claude)**; al detener, se quita.

**Desplazamiento.** La vista sigue cambiando de lugar al cambiar de párrafo, como hoy. Además, si el bloque es más alto
que la pantalla (código, tabla, párrafo largo) y la palabra marcada sale de la vista, se desplaza para mostrarla. Solo
si «seguir la lectura» está activo (`follow` en `lector.js`), así que no mueve la vista mientras el usuario lee otra
parte. En un párrafo que cabe en la pantalla no cambia nada.

**Tiempo.** `ReadAloudController` ya consulta la posición cada 60 ms y le suma `HighlightLead`; la palabra se calcula
igual que el segmento. Solo se llama a la página cuando cambia la palabra.

**Configuración.** Un `bool` nuevo en `AppSettings` (encendido por omisión). Apagado, la App no manda palabras a la
página. El texto y el lugar del interruptor en Configuración quedan al constructor.

---

## Parte B — Entonar comillas y paréntesis

### Para qué

En los documentos del usuario las comillas sirven **para nombrar algo**: el botón «Guardar», la opción "Usar", un
término. Hoy Azure las lee de corrido y no se oye que ese tramo es un nombre. Los paréntesis son apartes que hoy suenan
igual que el resto de la frase.

### Objetivos

1. Los tramos entre comillas suenan con el tono un poco más alto, **sin pausas**.
2. Los tramos entre paréntesis suenan como un aparte: tono un poco más bajo y un poco más rápido.
3. Siempre activo, **sin interruptor** (decisión del usuario).

### Fuera de alcance

- Kokoro y Piper: no aceptan SSML y partir la frase para meter pausas le cortaría la entonación. Suenan como hoy.
- Comillas simples ('…'): se confunden con el apóstrofo.
- Distinguir citas largas (diálogo) de nombres: todas las comillas se tratan igual.
- Cambiar lo que se dice: los signos se siguen leyendo (o callando) igual que hoy; solo cambia la entonación.

### Lo que no puede pasar

- **Que cambie el audio de un bloque sin comillas ni paréntesis.** Su SSML tiene que salir idéntico al de hoy, para
  no invalidar su caché ni gastar cuota otra vez. Lo mismo para Kokoro y Piper: el texto que reciben no cambia, así
  que su caché tampoco.
- Que una comilla sin cerrar o un `5"` (pulgadas) deje con otro tono el resto del párrafo.
- Prosodia anidada entre estilos de tramo (dos `<prosody>` de tramo uno dentro del otro): cuesta el doble. No cuenta
  el `<prosody rate>` que `SsmlBuilder` ya pone a todo el segmento cuando la velocidad global no es 0: los tramos nuevos
  siguen el mismo patrón que la negrita hoy, sin reestructurar `AppendSegment` **(decisión de Claude)**. Al probar,
  confirmar de oído que Azure combina el `rate` del aparte con el global en vez de reemplazarlo.
- Poner `<prosody>` en un tramo que solo tiene signos (por ejemplo los `«` `»` que quedan sueltos en `«**Guardar**»`,
  donde gana la negrita): esos tramos van sin etiqueta **(decisión de Claude)**.

### Cómo funciona

**Qué se reconoce.**

- Comillas: `«…»`, `“…”` y `"…"` rectas. La recta abre si va al inicio o tras espacio o signo de apertura, y cierra si
  va al final o antes de espacio o puntuación; no cuenta pegada a un dígito (`5"`).
- Paréntesis: `(…)`. Solo los más externos si hay anidados. Se dejan como están los que solo contienen un número, una
  letra o un numeral romano corto, como `(1)`, `(a)`, `(iv)` **(decisión de Claude)**.
- Una marca sin pareja dentro del bloque no hace nada.
- El código en línea (`RunStyle.Code`) no se toca, ni sus comillas cuentan.
- El tramo puede cruzar estilos: si abre en texto normal y cierra en cursiva, es un solo tramo.
- Los encabezados no llevan estilos de tramo hoy (`SsmlBuilder` los omite) y siguen igual.

**Dónde.** En `MarkdownSpeechParser`, después de juntar los tramos de un bloque (`CollectInlines`) y antes de partirlo
en segmentos, una pasada parte los `SpeechRun` en los bordes de cada comilla o paréntesis y les agrega un estilo nuevo
de `RunStyle` (por ejemplo `Quoted` y `Aside`). El texto concatenado del segmento no cambia: los signos quedan dentro
del tramo marcado.

**El SSML.** `ProsodyProfile` gana valores para los dos estilos y `ForRun` los usa. Valores de partida
**(decisión de Claude, se ajustan escuchando)**: comillas `pitch="+8%"` sin cambio de velocidad; paréntesis
`pitch="-6%" rate="+6%"`. Si un tramo tiene varios estilos se aplica uno solo, por prioridad: negrita, luego comillas o
paréntesis, luego cursiva **(decisión de Claude)**. Si hay comillas dentro de un paréntesis, como `(pulsa «Guardar»)`,
en el nombre gana el tono de las comillas: el aparte se cierra antes del nombre y se vuelve a abrir después (unos 110
caracteres en vez de 40, solo en estos casos). Lo mismo con paréntesis dentro de comillas: gana el de adentro.

**Costo.** Unos 31 caracteres de Azure por tramo entre comillas y unos 40 por paréntesis. Un documento con 50 tramos
gasta unos 1 500–2 000 caracteres más la primera vez. Aceptado por el usuario.

**Google** recibe la prosodia por `GoogleSsml`, que ya convierte `pitch` y `rate`.

**Efectos que hay que hacer en el mismo cambio.**

- La caché de los bloques con comillas o paréntesis se invalida (aceptado).
- `Core/Demo/asi-lee-shoken.md` contiene «Consejo»: `Demo_RecordingMatchesCurrentSsml` va a fallar. Hay que volver a
  grabar con `dotnet run empaquetado/generar-demo.cs` (~2 600 caracteres de Azure; pedirle permiso al usuario antes de
  gastarlos). Si `IntonationDemo.Describe` nombra los cambios de tono de cada parte, debe nombrar también estos, con
  las mismas reglas de `ProsodyProfile`.
- `README.md`: la tabla «Cómo se lee cada elemento» y el párrafo que dice que la cursiva y las citas no cambian la
  prosodia.
- `docs/ejemplo.md`: casos con cada tipo de comilla, paréntesis, `(1)`, comilla sin cerrar y `5"`.
- `AGENTS.md`: una regla breve para cada parte (dónde se reconocen las comillas; que los tiempos por palabra no son
  parte de la clave de caché).

---

## Lo que queda al constructor

- Nombres de tipos, mensajes entre la App y la página, y el lugar del interruptor en Configuración.
- El formato exacto de las palabras en la entrada de caché.
- El algoritmo concreto de emparejamiento, siempre que cumpla lo de arriba (en orden, hacia adelante, tolerante a
  palabras sin pareja).
- El color exacto de la palabra en cada tema, dentro del contraste exigido.
