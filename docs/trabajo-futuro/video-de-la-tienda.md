# Trabajo futuro: video de Shoken para la Microsoft Store

*Anotado el 25 de octubre de 2026, durante el primer envío a la Store (producto `9MWR6KCVN5HC`).*

La ficha admite tráileres (MP4 o MOV, 1920 × 1080 recomendado, hasta 2 GB, con una miniatura de 1920 × 1080) que se
muestran arriba de las capturas. Agregarlo después no requiere volver a certificar el paquete: basta un envío nuevo
con la ficha actualizada.

## Qué mostrar (45 a 60 s)

| Escena | Qué se ve | Título |
|---|---|---|
| 1 | Un documento leyéndose: el párrafo resaltado avanza y la vista lo sigue | «Lee Markdown con entonación» |
| 2 | La demo «Así entona Shoken»: el mismo texto con y sin entonación | «Escucha la diferencia» |
| 3 | Un apunte sobre una frase y «Copiar para la IA» | «Apuntes para tu IA» |
| 4 | El archivo cambia por fuera y Shoken lo recarga sin perder el párrafo; la tarjeta dice «La cita cambió» | «Se recarga solo» |
| 5 | La línea de lo escuchado y el ✓ en el índice | «Sabes qué ya escuchaste» |

## Cómo hacerlo sin tocar el mouse ni el teclado (lo puede hacer un agente)

- **Manejar la app:** lanzar Shoken con `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` y mover la
  página por DevTools. `window.lector` tiene `highlight`, `coverage.set`, `notes` y `scrollToBlock`, y los comandos de
  la app se mandan con `chrome.webview.postMessage({type: 'command', command})`. Antes, respalda `settings.json` y
  devuélvelo igual al terminar, y borra los apuntes de prueba de `apuntes\`.
- **Grabar la ventana:** `PrintWindow` con `PW_RENDERFULLCONTENT` (incluye el WebView2) y el proceso en modo
  *per-monitor DPI aware*, cuadro por cuadro (unos 10 a 15 cuadros por segundo, suficiente para lectura y resaltado).
  No hace falta traer la ventana al frente.
- **La voz:** en el equipo de desarrollo `MediaPlayer` puede no sonar. La grabación de la demo ya está en el repo
  (`src/Shoken.App/Assets/demo/demo-con.mp3` y `demo-sin.mp3`, voz Dalia de Azure) con el momento de cada segmento en
  `src/Shoken.Core/Demo/grabacion.json`. Para la escena 1 se puede mover el resaltado al ritmo de esas marcas y montar
  el audio encima, de modo que imagen y voz coincidan.
- **El montaje:** `ffmpeg` (no está instalado; `winget install Gyan.FFmpeg`) para unir los cuadros, pegar el audio y
  poner los títulos con `drawtext`.

## Límites conocidos

- La sesión de escritorio actual es de 1496 × 939: la ventana no llega a 1920 × 1080. Sale a 1440 × 810 y escalada a
  1080p se ve un poco blanda; para que salga nítida, sube la resolución de la sesión antes de grabar.
- No se ve un cursor moviéndose, porque nada se hace con el mouse.
- Costo estimado: de 2 a 3 horas de trabajo, más una o dos rondas de ajustes.

## Alternativa rápida

Grabarlo a mano con la Herramienta Recortes de Windows o Clipchamp mientras Shoken lee con audio real (unos 15
minutos, siguiendo la tabla de escenas). Sale con el cursor y la voz que se elija.
