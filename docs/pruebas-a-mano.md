# Pruebas a mano

Lo que no cubren las pruebas automáticas ni la autoprueba del exe y conviene repasar con la app abierta antes de publicar. Cada apartado dice qué hacer y qué tiene que pasar. Las pruebas automáticas se corren con `Probar.bat`; la autoprueba, con `Rodaje.bat -Autoprueba`.

## Aviso de versión nueva

La lógica (comparar versiones, 24 h, versión descartada) está probada en `tests/Rodaje.App.Tests/Updates/`. A mano se comprueba lo que depende de la red, de la ventana y del navegador.

1. **Arranque con red.** Borra `lastUpdateCheck` de `%LOCALAPPDATA%\Rodaje\ajustes.json` (o cierra la app y pon una fecha de hace más de un día), abre Rodaje y espera dos segundos. Si en GitHub hay una release más nueva que la del exe (Ayuda › Acerca de Rodaje...), la barra de avisos dice «Rodaje X.Y.Z disponible» con **Ver novedades** y **Más tarde**; si no la hay, no sale nada. En `ajustes.json` queda `lastUpdateCheck` con la hora en UTC. La ventana no se queda congelada mientras consulta.
2. **Ver novedades.** Abre en el navegador `https://github.com/Dathinel/rodaje/releases/tag/vX.Y.Z` y el aviso se cierra.
3. **Más tarde.** El aviso se cierra y `ajustes.json` guarda `dismissedUpdateVersion: "vX.Y.Z"`. Cierra y abre Rodaje con `lastUpdateCheck` borrado: no vuelve a avisar de esa versión. Con una versión más nueva aún publicada sí avisaría.
4. **Una vez al día.** Con `lastUpdateCheck` de hace menos de 24 h, al arrancar no hay consulta (sin tráfico a `api.github.com`, por ejemplo mirando el Monitor de recursos) ni aviso.
5. **Sin red.** Desconecta la red (o apunta `api.github.com` a `127.0.0.1` en `hosts`) y arranca con `lastUpdateCheck` borrado: ningún aviso, nada en `app\rodaje-error.txt` ni en el registro de la sesión, y la app arranca igual de rápido. `lastUpdateCheck` queda anotado igual (no insiste en cada arranque).
6. **Ayuda › Buscar actualizaciones...** Consulta aunque hayan pasado menos de 24 h, el aviso esté apagado o la versión esté descartada. Con una versión nueva, el mismo aviso que al arrancar; con la última instalada, «Tienes la última versión, X.Y.Z» en verde; sin red, «No se pudo comprobar» en ámbar. Pulsarlo dos veces seguidas no abre dos consultas.
7. **Preferencias › General › «Avisar cuando haya una versión nueva».** Desmárcalo y guarda: `checkForUpdates: false` en `ajustes.json` y al arrancar no se consulta (ni con `lastUpdateCheck` borrado). Vuelve a marcarlo y guarda: de fábrica está marcado.
8. **Autoprueba y mediciones.** `Rodaje.bat -Autoprueba` y `-Medir` no consultan nunca (ninguna petición a `api.github.com`).
