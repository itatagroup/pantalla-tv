# Cómo contribuir

¡Gracias por querer ayudar! Los issues y los pull requests son bienvenidos, **en español**.

## Antes de empezar

- Para un error o una idea, abre un issue contando qué pasa y en qué PC o TV (versión de Windows, gráfica, modelo
  de la tele). La guía `docs/prueba-otro-pc.md` explica cómo sacar un diagnóstico útil.
- Para un cambio grande, abre un issue primero y conversemos el enfoque antes de que gastes tiempo.
- Las vulnerabilidades **no** van en un issue: ver `SECURITY.md`.

## Pull requests

- Parte de la rama `dev` y abre el PR **contra `dev`** (`main` es la rama estable, solo para versiones publicadas).
- El mantenedor revisa y aprueba todo; puede pedir cambios o rechazar lo que no encaje con el proyecto.
- Un PR, un tema. Explica qué cambia y por qué, y qué probaste.

## Reglas

1. **Sin telemetría ni conexiones nuevas a internet.** Pantalla TV solo habla por la red local (ver el README). La única excepción prevista es la búsqueda de actualizaciones, opcional y que el usuario puede apagar.
   Un cambio que agregue una conexión externa, un servicio de terceros, cuentas o análisis de uso no se acepta.
2. **Apps de Android sin dependencias** (nada de AndroidX ni librerías): los APK deben seguir siendo diminutos.
3. **Pruebas en verde**: `dotnet test emisor-windows/PantallaTv.sln -c Release` y, si tocas Android, los dos builds
   (`node tools/build.mjs` en `receptor-android/` y en `emisor-android/`). Agrega pruebas a lo que cambies. Las
   advertencias del compilador son error.
4. **Nunca subas claves**: ni `keystore/`, ni `*.jks`, ni contraseñas, ni IP o datos de tu casa en ejemplos
   (usa `10.0.0.x`).
5. Latencia primero: si un cambio afecta la captura, la codificación o el envío, mídelo y cuenta el número.
6. Textos de pantalla, documentación y mensajes de commit en español.

## Licencia

Al contribuir aceptas que tu aporte se publique bajo la **GPL-3.0**, la licencia del proyecto (ver `LICENSE`).
