# Política de seguridad

## Cómo reportar una vulnerabilidad

**No abras un issue público.** Usa el reporte privado de GitHub: en la pestaña *Security* del repositorio,
**«Report a vulnerability»**. Así el reporte queda visible solo para el mantenedor hasta que haya una corrección.

Incluye, si puedes: la versión (del emisor, del receptor o del instalador), los pasos para reproducirlo y el
efecto que tiene.

## Qué cubre

- El emisor de Windows (`PantallaTv.exe`), su instalador y desinstalador (que escriben en el registro del usuario y
  pueden pedir permisos de administrador).
- El controlador del monitor virtual **que se incluye** en el instalador (la instalación y el desinstalado los hace
  este proyecto; fallas del controlador en sí también se pueden avisar en el proyecto original, ver `THIRD-PARTY.md`).
- El receptor de Android («Pantalla») y el emisor de Android («Enviar a Pantalla»).
- El protocolo entre ambos (`docs/protocolo.md`).

## Lo que ya se sabe

El protocolo funciona **solo en red local**, **sin cifrar** y sin autenticación obligatoria (el campo de PIN existe
pero no se exige). Cualquiera que esté en tu red local puede conectarse al receptor y mostrar imagen en la tele.
Esto es una limitación conocida y documentada, no una vulnerabilidad nueva; úsalo en redes de confianza.

## Qué esperar

Es un proyecto mantenido por una persona. Se responde a los reportes con la mayor rapidez posible, sin plazos
garantizados, y se agradece la divulgación coordinada.
