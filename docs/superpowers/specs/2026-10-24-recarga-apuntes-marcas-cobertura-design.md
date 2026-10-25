# Recarga automática, apuntes que siguen al texto, marcar sin parar y cobertura — Diseño

Fecha: 2026-10-24. Estado: aprobado por el usuario el 2026-10-24.

Las decisiones del usuario van sin marca. Las que tomó Claude y el usuario todavía no revisó van marcadas
**(decisión de Claude)**.

## Para qué

El usuario revisa documentación con una IA en rondas. Escucha un `.md` en Shoken, deja apuntes, los copia para Claude,
Claude edita el archivo y el usuario vuelve a escucharlo. Estas cuatro mejoras quitan fricción de esa vuelta:

1. **Recarga automática:** el documento se actualiza solo cuando Claude lo guarda.
2. **Apuntes que siguen al texto:** si Claude retoca el texto citado, el apunte no queda huérfano. Muestra cómo era y
   cómo quedó.
3. **Marcar sin parar:** con una tecla, el párrafo que suena queda marcado para revisar, sin pausar ni escribir.
4. **Cobertura:** se ve qué partes del documento ya se escucharon.

Son independientes entre sí. Se pueden construir y entregar por separado, en este orden: 1 y 2 primero porque se
apoyan (la recarga vuelve a ubicar los apuntes), luego 3 y 4.

Relación con otro diseño: `2026-10-24-cambios-desde-lo-escuchado-design.md` (marcar lo que cambió) está escrito y sin
aprobar. La cobertura (punto 4) registra qué bloques sonaron. Ese registro tiene que quedar listo para que aquel diseño
lo reutilice: misma carpeta y misma definición de «escuchado».

## Reglas que valen para las cuatro

- **No se toca el audio:** ni `SsmlBuilder`, ni el normalizador, ni `ChunkPlanner`. Las claves de caché y el plan de
  bloques quedan iguales. Ninguna de estas mejoras puede gastar cuota de Azure por sí misma.
- **Se compara el texto del documento, no el que se dice.** `SpeechSegment.PlainText` es el texto para la voz: un
  bloque de código dice «Bloque de código en C#.», las filas de tabla llevan el nombre de la columna, las listas
  numeradas su número, y todo eso depende de las opciones de Configuración. Donde haga falta reconocer un bloque entre
  versiones (cobertura, retomar), se usa un texto que **no cambie con las opciones de lectura ni con el diccionario**,
  por ejemplo el Markdown fuente del bloque (`Line`..`EndLine`). Quien lo construya elige cuál.
- **La unidad visible es el bloque de la página** (lo que lleva `data-seg`: párrafo, título, elemento de lista, fila,
  bloque de código). Un párrafo largo que el parser parte en varios segmentos comparte `AnchorId` y es un solo bloque.
- **Las rutas de datos salen de `AppPaths`** (funciona igual empaquetada en MSIX).
- **Solo archivos `.md`.** Los textos pegados no se vigilan ni guardan cobertura **(decisión de Claude)**: nadie los
  edita fuera de la app.
- Lógica en `Shoken.Core` con pruebas xUnit (`Componente_Comportamiento`). Textos en español, identificadores en inglés,
  clases `sealed`, tamaños de texto con `{DynamicResource LectorFontSize…}`, colores en `ThemePalettes` (ver
  `AGENTS.md`).
- Actualizar el `README.md` (tabla de Uso, la sección de apuntes y «Datos que guarda») y `AGENTS.md` si aparece una
  regla que no se ve a simple vista. `dotnet test` en verde.

---

## 1. Recarga automática

### Comportamiento

Shoken vigila el archivo de la **pestaña activa**. Las demás no hace falta vigilarlas, porque `ActivateAsync` ya vuelve a
leer el archivo al activar la pestaña.

Cuando el archivo cambia en disco:

- **Si no está sonando** (detenido o en pausa), recarga enseguida y en silencio. Muestra un aviso breve: «Documento
  actualizado». La pausa sigue en el mismo párrafo.
- **Si está sonando**, no corta. Muestra un aviso con tres opciones:
  - **Recargar ahora:** recarga y sigue leyendo desde el mismo párrafo.
  - **Al pausar:** recarga sola la próxima vez que pauses o se detenga la lectura.
  - **Ahora no:** cierra el aviso. Si el archivo vuelve a cambiar, el aviso vuelve a salir. F5 sigue funcionando.

  El usuario pidió «avisar y dar opciones». Las tres opciones y que sin sonido se recargue solo son **(decisión de
  Claude)**.
- **Si está sonando y la ventana no está al frente** (detrás de otra ventana, minimizada o en la bandeja), vale «Al
  pausar» sin preguntar. El aviso con opciones queda esperando en la ventana para cuando el usuario vuelva. Si el mini
  reproductor está visible, muestra «Se actualiza al pausar».

Detalles:

- **Esperar a que el editor termine de escribir.** Los editores escriben en varias pasadas, o escriben un temporal y lo
  renombran. Se espera un momento (unos cientos de milisegundos) y se ignoran los eventos que no cambian el contenido:
  si el texto es igual al cargado, no se recarga.
- **Archivo borrado o renombrado:** la pestaña sigue abierta con lo último que se cargó y se avisa «El archivo ya no
  está en su carpeta» **(decisión de Claude)**.
- **La recarga automática no vuelve a cargar el diccionario** de pronunciación; F5 sí, como hoy.
- **Si se está escribiendo un apunte**, la recarga espera a que se guarde o se cancele la tarjeta. Recargar vuelve a
  cargar la página entera y se perdería lo escrito **(decisión de Claude)**.
- **Los apuntes se vuelven a ubicar** y la cobertura se recalcula, como en cualquier carga.

### Retomar en el párrafo correcto

Hoy el párrafo para seguir leyendo se guarda por número (`OpenTab.ResumeSegmentId`, y `ReloadAsync` usa
`CurrentSegmentId`). Si Claude inserta texto antes, al recargar se retoma en otro lado. Ahora, al recargar (automático,
F5 o al volver a una pestaña cuyo archivo cambió):

- Se busca el bloque con el mismo contenido más cercano a la posición anterior. Si no está (porque ese mismo párrafo
  cambió), se usa el bloque que ocupa su lugar y, en último caso, el número.
- **Se conserva lo que el usuario estaba mirando** **(decisión de Claude)**: si el documento cambió, el desplazamiento en
  píxeles (`ScrollY`) ya no sirve. La vista se ubica por el primer bloque visible antes de recargar (o por el párrafo de
  retomar, si se estaba leyendo).

El aviso con botones puede seguir el patrón del aviso de «Deshacer» de los apuntes (en la página) o ser un elemento
WPF. Quien lo construya elige, cuidando la regla de foco de `AGENTS.md`.

---

## 2. Apuntes que siguen al texto

### Comportamiento

Hoy cada apunte se ubica buscando su cita exacta (sin acentos) en la página. Si no aparece, el apunte queda huérfano.
Ahora hay tres resultados posibles:

| Resultado | Cuándo | Qué se ve |
|---|---|---|
| **Igual** | La cita aparece tal cual | Como hoy |
| **La cita cambió** | No aparece tal cual, pero cerca de la línea guardada hay un texto parecido | El apunte se ancla al texto nuevo |
| **Huérfano** | No hay nada parecido cerca | Como hoy |

La tarjeta de un apunte cuya cita cambió:

- Muestra la señal **«La cita cambió»**.
- Muestra la **cita original** (atenuada o tachada) y el **texto actual**.
- ▶ lee desde el texto actual, para **escuchar cómo quedó**.
- **Resolverlo sigue siendo decisión del usuario:** nada se resuelve solo.

Se guarda la cita original. El texto actual se recalcula cada vez que se abre el documento, como hoy el estado de
huérfano, que no se guarda. Si el texto vuelve a coincidir con la cita, el apunte vuelve a «Igual».

**Copiar para la IA**, para un apunte cuya cita cambió, manda las dos versiones: «Cita original: …» y «Ahora dice: …».

**Huérfanos en bloque** **(decisión de Claude)**: si hay apuntes pendientes huérfanos, la columna de apuntes ofrece
«N apuntes ya no encuentran su cita · Marcar resueltos», con aviso para deshacer.

### Qué cuenta como parecido

- **Orden de búsqueda** **(decisión de Claude)**: primero la cita exacta cerca de la línea guardada, después un texto
  parecido cerca, y por último la cita exacta en cualquier parte del documento. Así, un párrafo retocado sigue con su
  apunte aunque su cita corta aparezca tal cual en otra sección. Un párrafo que se movió intacto a otra sección se
  sigue encontrando, salvo que cerca quede algo parecido. Se elige este orden porque retocar el texto citado es lo que
  más hace la IA en estas rondas, y mover párrafos intactos es menos común.
- El parecido solo se busca **cerca de la línea guardada**, por ejemplo en la misma sección o en una ventana de líneas
  alrededor, para no pegar el apunte a un párrafo lejano que se parezca.
- Hay un **umbral de parecido**. Un retoque de algunas palabras tiene que encontrarse. Un párrafo reescrito por completo
  no, y queda huérfano.
- La cita puede ser una selección parcial dentro de un párrafo. En ese caso se busca el tramo más parecido de largo
  similar.
- Se compara sobre el texto que se ve en la página, como hoy (`textIndex`).

El algoritmo y el umbral los elige quien lo construya. Lo importante es que la lógica quede probada. Como el repo solo
tiene pruebas xUnit de Core y el anclaje vive en `lector.js`, hay dos caminos: llevar la comparación de parecido a Core
y probarla ahí, o dejarla en la página y probarla a mano con DevTools (ver la memoria «probar el WebView por
DevTools»).

---

## 3. Marcar sin parar

### Comportamiento

La tecla **`M`** marca para revisar el párrafo que se está leyendo, sin pausar y sin abrir la tarjeta para escribir.
Funciona con la ventana de Shoken al frente y cuando no se está escribiendo (ni en la tarjeta de un apunte, ni en la
búsqueda, ni en un cuadro de texto). Tiene que andar tanto si el foco de teclado está en WPF como en el documento
(`lector.js`), como los demás atajos.

- **La marca es un apunte sin nota**, de tipo *Aclarar*, sobre el bloque entero que suena **(decisión de Claude)**. La
  tarjeta compacta lo muestra como «Por revisar · sin nota». Al desplegarla se escribe la nota como en cualquier
  apunte.
- **Sin lectura en curso:** si hay un párrafo actual (donde quedó la lectura), marca ese. Si no, avisa «Empieza a
  leer para marcar» **(decisión de Claude)**.
- **Confirmación** sin mirar mucho: un aviso breve en la ventana, «Marcado para revisar», y la tarjeta destella. No hay
  sonido.
- **No se duplica:** si ese bloque ya tiene un apunte pendiente con la misma cita, avisa «Ya estaba marcado» y no crea
  otro **(decisión de Claude)**.
- **Si los apuntes están ocultos**, no se fuerza a mostrarlos (sería otra interrupción). Se actualiza el número del
  botón de Apuntes y el aviso lo dice **(decisión de Claude)**.
- **Copiar para la IA** exporta las marcas sin nota como cualquier apunte, con «(sin nota)», como ya hace el
  exportador.

Hay que verificar que `M` no choque con otro atajo, por ejemplo con el menú de la pestaña o con la tecla de acceso de
algún botón.

---

## 4. Cobertura

### Qué cuenta como escuchado

Un bloque cuenta como escuchado cuando **suena de principio a fin sin saltos**: si tiene varios segmentos, todos. Si se
salta con +10 s o con otra navegación, no cuenta. Empezar desde ese bloque (doble clic, ▶ del índice, retomar) sí parte
de su principio. Es la misma definición del diseño de «cambios desde lo escuchado».

- **Un bloque que cambió deja de contar como escuchado**, porque se reconoce por su contenido (ver «Reglas que valen
  para las cuatro»). Uno que solo se movió de lugar sigue contando.
- **Bloques que no se escuchan completos** (código anunciado, diagramas, fórmulas, lo omitido por las opciones):
  cuentan como escuchados cuando suena lo que tienen (su anuncio), o se excluyen del cálculo si no suenan
  **(decisión de Claude)**. La cobertura mide lo que se oye, no lo que se revisó a la vista.
- **Se guarda qué sonó, no la conclusión** **(decisión de Claude)**: por ejemplo, que un bloque de código sonó solo su
  anuncio. Así cada diseño aplica su regla: la cobertura lo cuenta como escuchado y «cambios desde lo escuchado» no
  apaga la marca de un bloque así.
- **Se guarda** por documento en la carpeta de datos, un archivo por documento nombrado por un hash de la ruta, como
  los apuntes: `escuchado\<hash de la ruta>.json`, con una propiedad nueva en `AppPaths`. Se guarda con un retardo corto
  para no perderlo si la app se cierra. Un archivo dañado se trata como «nada escuchado». Si el `.md` se mueve, se
  pierde, igual que los apuntes.
- **Olvidar lo escuchado:** una opción en el menú de la pestaña lo reinicia **(decisión de Claude)**.

### Cómo se ve: sutil, en tres lugares

El usuario eligió verlo **en el índice, en la línea de tiempo y en el documento, de forma sutil**.

- **Documento:** una línea fina y tenue en el margen izquierdo de cada bloque escuchado. Tiene que leerse como un
  detalle de fondo, no como una marca: más tenue que la barra de lectura y que cualquier marca de apuntes. Va en el
  mismo lugar del margen donde el otro diseño pone la barra de «cambió» o «nuevo». No chocan, porque un bloque cambiado
  por definición no está escuchado.
- **Índice:** cada sección muestra lo escuchado sin agregar ruido. Por ejemplo, una línea de progreso fina bajo la fila
  y un ✓ discreto junto a la duración cuando está completa **(decisión de Claude sobre la forma)**. Una sección
  contraída muestra la cobertura de toda la sección.
- **Línea de tiempo del reproductor:** los tramos escuchados con un tono apenas distinto del resto. Tiene que
  distinguirse del avance de la reproducción, que ya se pinta con el color de acento.

Colores: en `ThemePalettes`, para los cuatro temas en claro y oscuro. Son tenues a propósito y la información también
está en el índice, así que no se les exige el 3:1 de los elementos que hay que ver para entender. Sí tienen que ser
perceptibles: `ThemeTests` debe verificar un contraste mínimo bajo contra el fondo (algo como 1.3:1) **(decisión de
Claude)**. Los colores que ya existen no se tocan ni se baja ningún umbral.

---

## Lo que queda a criterio de quien lo construya

- El mecanismo para vigilar el archivo y cuánto esperar antes de recargar.
- El aviso con botones de la recarga (en la página o en WPF).
- El algoritmo de parecido para los apuntes, su umbral y la ventana de búsqueda.
- El texto con que se reconoce un bloque entre versiones, siempre que no dependa de las opciones de lectura ni del
  diccionario.
- El formato de `escuchado\<hash>.json`. Tiene que poder crecer para el diseño de «cambios desde lo escuchado».
- La forma exacta de la línea de cobertura, el progreso en el índice y el tono en la línea de tiempo, dentro de «sutil».
- Cómo detecta el reproductor que un bloque sonó completo, sin saltos.
