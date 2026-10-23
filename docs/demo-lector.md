# Lector MD: guía rápida

Lector MD lee tus archivos Markdown en voz alta y usa su formato para decidir **dónde hacer pausas** y *cómo entonar*. Esta página es una demostración: escúchala mientras la lees.

> [!TIP]
> Haz doble clic sobre cualquier párrafo para empezar a leer desde ahí.

## Cómo empezar

1. Abre un archivo con extensión `.md`.
2. Pulsa la barra espaciadora para leer o pausar.
3. Usa Control y las flechas para saltar entre secciones.

Antes de tu primera lectura, revisa esta lista:

- [x] Configurar la clave de Azure
- [ ] Descargar una voz de Piper para trabajar sin conexión

## Motores de voz

| Motor | Calidad | ¿Funciona sin internet? |
|---|---|---|
| Azure | Muy alta | ❌ |
| Piper | Buena | ✅ |

Cuando se acaba la cuota gratuita del mes, la lectura **no se detiene**: continúa con Piper → sin que tengas que hacer nada.

```powershell
dotnet run --project src/LectorMd.App
```

---

## Una última recomendación

> Lo que ya escuchaste se guarda en caché, así que repetirlo no gasta cuota.

Si alguna palabra en inglés suena rara, como README o JSON, agrégala al diccionario de pronunciación, p. ej. "Docker" se puede escribir "dóker". Más detalles en [la documentación](https://github.com).
