# Repaso con ratón, vuelta 4 (23-nov)

Exe AOT de `main` (e02b256a, con B16) y después con los dos arreglos de #725. Dos instancias en el mismo PC, cada una con su
`RODAJE_DATOS` y la carpeta de salas en un temporal, conexión directa (2–10 ms). Nada se escribió en Documentos ni en Vídeos.

## Comprobado

| Qué | Resultado |
|---|---|
| Tamaño raro (#700) | Clip 1080×2400: aviso «La secuencia tomó 1080×2400 del clip» y «Usar 1080×1920» la deja en 9:16. |
| Coma y punto (#709) | Exportar › tasa: `1.5` → 1,50; `2,5` → 2,50; `1.000` → 1,00. Preferencias › imágenes fijas: `2.5` → 2,5. |
| Caso del amigo (#687) | Clip vertical, 60 fps y 1920×1080 en Exportar: no se cierra; el archivo sale 1920×1080, 60/1, h264 + aac. |
| Preferencias en ventana 1280×720 (#711) | Cabe, con Guardar y Cancelar a la vista. |
| Cerrar con un desplegable abierto (#723) | «Ámbito» abierto + cerrar: el proceso termina y `fallos\sesiones` queda vacío. |
| Unirse y buscar medios | Invitación, cuatro palabras iguales en los dos, aprobación y «Busca tus archivos». |
| Pedir el original | Aprobación del anfitrión, barra «Paso 1 de 3…», «Original recibido»; desde la biblioteca, sin volver a preguntar lo ya aprobado. |
| Copia ligera | «Copia ligera lista»: se ve en la línea de tiempo del invitado. |
| Exportar en el anfitrión (B16) | Con un medio solo en copia ligera, Exportar se bloquea y ofrece «Exportar en el anfitrión»; el anfitrión aprueba y sale `colab.mp4` (1920×1080, 20 s) en su carpeta de la sala en 6 s. |
| Pedir la pista | Al editar una pista ajena: «Ana tiene esa pista ahora… Lo deshicimos» con «Pedir la pista»; tras pedirla, «puedes editarla» y el otro ve «Luis edita aquí». |
| Pasar anfitrión (con #725) | Menú «…» estable, confirmación, traspaso; la edición sigue sincronizada. |
| Cerrar la sala para todos | El invitado ve «La sala terminó. Ana cerró la sala» con la ruta de su copia. |
| Preferencias › Colaboración | Relé, «Solo por relé», nombre, carpeta de copias y tope de originales. |
| Cerrar con sala abierta | Avisos distintos para invitado y anfitrión; las dos instancias cierran sin informe de fallo. |

## Encontrado

| # | Qué | Estado |
|---|---|---|
| D1 | «Busca tus archivos»: un segundo botón cancelaba en silencio la petición en curso; el invitado veía «Ya tienes otro original pendiente», el anfitrión aprobaba y no llegaba nada. | Arreglado en #725: una operación a la vez, los demás botones esperan. Comprobado con ratón. |
| D2 | Panel de la sala: el diagnóstico de cada segundo rehacía el panel entero; el menú «…» de una persona se cerraba solo y «Pasar anfitrión a…» no hacía nada. | Arreglado en #725: solo cambia el texto. Comprobado con ratón. |
| D3 | Tras pasar el anfitrión, el ex anfitrión ve al nuevo «Desconectado» y «1 persona», y el nuevo anfitrión se ve «quien hospeda · suplente» mientras el otro también se cree suplente. | Corregido en #726. |
| D4 | Con la ventana a 960 px, la barra de menús corta «Ayuda» («Ay») y la barra de la línea de tiempo esconde «Ámbito». | Corregido en #728. |
| D5 | El recorrido inicial («1 · Importa tus medios») salta sobre el invitado al unirse a una sala y sobre el aviso de tamaño raro. | Corregido en #727. |
| D6 | Al pedir exportar en el anfitrión, la fase del invitado sigue en «Pidiendo permiso…» con la barra ya en 1 %. | Corregido en #727. |
| D7 | «Mostrar en el Explorador» sigue activo en un medio sin conexión. | Corregido en #727. |

No probado aquí: relé propio, «Solo por relé» de verdad, otro país, OneDrive «solo en la nube», red caída, reinicio de la GPU y
la Intel (el portátil la tiene apagada); siguen en `pruebas-a-mano.md`.
