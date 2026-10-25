# Política de privacidad de Shoken

*Vigente desde el 25 de octubre de 2026.*

Shoken es una app de Windows que lee en voz alta archivos Markdown. No tiene cuentas, no muestra anuncios y no envía
telemetría ni estadísticas de uso a nadie. Su autor no recibe ningún dato tuyo.

## Qué sale de tu equipo

Solo sale lo necesario para generar la voz o descargar algo que pediste:

| Cuándo | A dónde | Qué se envía |
|---|---|---|
| Lees con **Azure** (si pusiste tu clave) | Microsoft Azure Speech, en la región que elegiste | El texto del documento que se va a leer, con tu clave |
| Lees con **Google** (si pusiste tu clave) | Google Cloud Text-to-Speech | El texto del documento que se va a leer, con tu clave |
| Consultas el **consumo real de Azure** en Configuración | Microsoft (inicio de sesión y Azure Resource Manager) | Tu inicio de sesión de Microsoft, para leer el consumo de tu propio recurso |
| Descargas el **modelo de Kokoro** en Configuración | GitHub (`github.com/Lyrcaxis/KokoroSharpBinaries`) | Solo la petición de descarga; no se envía nada tuyo |

El texto se envía con **tu** clave y queda sujeto a la política de privacidad de ese servicio (Microsoft o Google), no
a la de Shoken. Si no configuras ninguna clave, Shoken lee con sus voces locales (Kokoro y Piper) y el texto nunca
sale de tu equipo.

## Qué se guarda en tu equipo

Todo se guarda en la carpeta de datos de la app, en tu equipo:

- la configuración y las pestañas abiertas, con las claves de Azure y Google cifradas con la protección de datos de
  Windows (DPAPI);
- el audio generado (caché), tus apuntes, lo que ya escuchaste de cada documento, tu diccionario de pronunciación y el
  conteo de caracteres enviados a Azure este mes;
- un registro de errores inesperados, que no sale de tu equipo.

En la versión de la Microsoft Store, Windows borra esa carpeta al desinstalar la app.

## Portapapeles

Shoken solo usa el portapapeles cuando tú lo pides: lo lee al pegar (`Ctrl`+`V`) y lo escribe al pulsar «Copiar para
la IA».

## Niños

Shoken no está dirigida a menores de 13 años y no recoge datos de nadie.

## Cambios y contacto

Si esta política cambia, se actualizará este archivo y su fecha. Para preguntas, abre un issue en
<https://github.com/omarqui/shoken-lector-md/issues>.

---

## Summary in English

Shoken has no accounts, ads or telemetry, and its author receives no data. Document text leaves your PC only when you
choose a cloud voice: it is sent with your own key to Microsoft Azure Speech or Google Cloud Text-to-Speech, under
their privacy policies. Signing in to view your Azure usage talks to Microsoft. Downloading the Kokoro model fetches a
file from GitHub. Settings (with keys encrypted by Windows DPAPI), the audio cache, notes, listening progress and the
error log stay on your PC; the Microsoft Store version removes them when you uninstall it. Questions:
<https://github.com/omarqui/shoken-lector-md/issues>.
