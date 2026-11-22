# Repaso con ratón de Rodaje, vuelta 3 (22-nov)

Exe de `main` `1.0.4+0b639beb3c12` (B11, B14 y B15a unidos), compilado con `build\app.ps1` y `dathinel-red` puesto a mano (ver C1). Dos copias en el mismo PC, cada una con su `RODAJE_DATOS` (identidad propia): anfitrión e invitada, a media pantalla cada una. Clips de prueba sintéticos (testsrc2 y Mandelbrot con tono). Sesión de 12:16 a 12:41. ✔ = comprobado; ✘ = fallo; NP = no probado o no concluyente.

## 1. Colaboración

| Paso | Resultado | Detalle |
|---|---|---|
| Invitar (Colaborar › Invitar a otras personas…) | ✔ | Nombre, permiso, caducidad y «un solo uso»; «Invitación lista» con «Copiar invitación». La barra superior pasa a «Sala · 1 persona». |
| Unirse (Colaborar › Unirse a una sala…) | ✔ | «Pegar», nombre y carpeta; barra «Paso 1 de 4: conectando… Llevas 0:04» con Cancelar; cuatro palabras (tambor-rutina-ropa-usual). |
| Aprobar en el anfitrión | ✔ | «Sala · 1 en espera» abre el panel con «Invitada quiere entrar» y las mismas cuatro palabras; «Dejar entrar para editar». |
| Panel de la sala | ✔ | 2 personas, camino directo, ida y vuelta 13 ms, pérdida 0 %, versiones iguales (1.0.4). |
| Presencia | ✔ | Cabezal ajeno con nombre y color; «X está aquí» y «Reservado para ti» en las cabeceras. |
| Cambio del anfitrión visible en la invitada | ✔ | clip-b a Video 2: en la invitada en menos de 3 s. |
| Pista reservada por otro | ✔ con ✘ | La invitada no puede mover clip-b en Video 2: «Anfitrión tiene esa pista ahora… o pídesela. Lo deshicimos…». ✘ No hay forma de «pedírsela» (C5). |
| Cambio de la invitada visible en el anfitrión | ✔ | Opacidad de clip-a al 50 %: el visor del anfitrión se oscurece. |
| Ctrl+Z solo de lo propio | ✔ | Deshace la opacidad en los dos; clip-b del anfitrión sigue en Video 2. |
| Copia ligera (B14) | ✘ grave | Barra «Paso 1 de 2… %» con Cancelar y aviso «Copia ligera lista: clip-a ya se ve y se oye». El proxy (4,8 MB) está en la carpeta de la sala, pero el visor sigue negro al mover el cabezal y al reproducir, y la biblioteca sigue en «Sin conexión» (C2). |
| Exportar en la invitada | ✔ | Desactivado con «clip-a solo tiene su copia ligera de la sala. Exporta en el anfitrión.» |
| Cerrar la sala para todos | ✘ | El anfitrión queda en «No estás en ninguna sala»; la invitada ve «Sala · sin conexión» y «Se perdió la conexión… Rodaje reintenta solo» (C3). |
| Cerrar la invitada | ✔ | Pregunta guardar y después «Hay una sala abierta»; se cierra en unos segundos. |

## 2. Resto

| Prueba | Resultado | Detalle |
|---|---|---|
| Cancelar una exportación | ✔ | 4K de 3:12: «7 % · 100 fps · quedan unos 55 s», la X la cancela («Cancelado», «Volver a exportar»); el archivo anterior con el mismo nombre queda intacto y no deja temporales. |
| Alta de keyframes con la reproducción en marcha | ✔ | Tres altas de Rotación mientras reproduce: de 2 a 5 keyframes, sin tirones. |
| Zoom de la gráfica con la rueda | NP | La rueda sola desplaza el panel (así está hecho); el zoom usa el modificador de la línea de tiempo, que este control no entrega. Para Daniel con ratón real. |
| Exportar e importar FCPXML | ✔ | Informe de pérdidas antes de exportar; importar: «6 clips en un clip compuesto en pistas nuevas» con Deshacer, que lo quita en un paso. |

## Hallazgos (por gravedad)

- **C1 (publicación, arreglado):** `cargo deny` fallaba por RUSTSEC-2026-0592 (`url` 2.5.8) y `build\red.ps1` no empaquetaba `dathinel-red`: `distribuir.ps1` habría fallado al publicar. Sala#25 sube `url` a 2.5.9; falta subir el submódulo en Rodaje.
- **C2 (grave):** la copia ligera llega pero no se reproduce.
- **C3 (grave):** «Cerrar la sala para todos» se presenta a la invitada como un corte que se reintenta.
- **C4 (medio):** a media pantalla (~960 px) desaparece el botón EXPORTAR de la barra superior y el panel de la sala se corta.
- **C5 (medio):** «pídesela» sin forma de pedir la pista.
- **C6 (medio):** al unirse sin medios salen dos diálogos seguidos («Busca tus archivos» y «Faltan 2 medios»), el segundo con ids internos (`media-f914…`) en vez de nombres.
- **Leves:** el panel de la invitada nombra al anfitrión por sus palabras de comprobación; «Generar proxy» activo en un medio sin conexión; la gráfica abre en «Opacidad» aunque solo Rotación tiene keyframes; el informe de FCPXML dice «rotation» en vez de «Rotación».
- **Docs:** `prueba-colaboracion.md` cita menús que no existen (Archivo › Colaborar › Hospedar, Estado de la conexión, código QR).
- **Solo de desarrollo:** `app.ps1` no deja `dathinel-red` en `dist` y la ruta se resuelve al arrancar (hay que reiniciar si falta).

Los hallazgos C2-C6, leves y docs están encargados al jefe de colaboración.

## Cómo quedó el PC

Las dos copias cerradas sin guardar; borrada `Documentos\Rodaje` (la creó la prueba); los proyectos y clips de prueba están en el scratchpad de la sesión; quitada mi línea de `medicion-en-curso.txt`.
