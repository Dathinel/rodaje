# Guion de la prueba de colaboración entre dos países

Este documento explica, paso a paso y de manera sencilla, cómo probar la función de colaboración de Rodaje 1.0.4 entre dos personas ubicadas en países distintos conectadas a través de internet.

## 1. Antes de empezar

1. Qué se prueba: dos personas van a editar el mismo proyecto de video a la vez a través de internet.
2. Cuánto dura: la prueba toma entre 45 y 60 minutos en total.
3. Qué se necesita: un ordenador con Windows 10 o Windows 11 de 64 bits y una conexión a internet normal en cada lado.
4. Comunicación: una llamada de voz o videollamada abierta durante toda la prueba (por micrófono o teléfono) para hablar continuamente.
5. Regla fundamental: si algo falla o no responde, no es culpa de nadie; se anota lo ocurrido y se sigue adelante con la prueba.

## 2. Qué instalar

1. Versión requerida: Rodaje 1.0.4 en los dos ordenadores. Es obligatorio que ambos equipos tengan exactamente la misma versión. Si no coinciden, Rodaje avisará indicando quién debe actualizar.
2. De dónde descargar: desde la página de lanzamientos de GitHub `https://github.com/Dathinel/rodaje/releases`, eligiendo el instalador `Rodaje-1.0.4-instalador.exe` o la versión portátil en archivo zip.
3. [Daniel: confirma el enlace antes de enviar este guion] (si la versión 1.0.4 aún no está publicada en GitHub, Daniel enviará el archivo directamente por otro medio).
4. Cómo verificar la versión: abre Rodaje y entra en el menú **Ayuda › Acerca de Rodaje...** `[confirmar nombre del menú]` para comprobar que ambos leen la versión 1.0.4.

## 3. El cortafuegos de Windows

La primera vez que alguien hospeda una sala o se une a una, Windows puede mostrar una ventana del Firewall de Windows Defender indicando que bloqueó algunas características de `dathinel-red.exe` (el programa de red de Rodaje).

1. Qué responder: marca la casilla **Redes privadas** (la red de casa o del trabajo) y haz clic en **Permitir acceso**. Marca «Redes públicas» únicamente si estás conectado en un sitio público como una cafetería y no hay otra opción.
2. Si pulsaste Cancelar o Bloquear sin querer: ve al menú Inicio de Windows, escribe «Permitir una aplicación a través del Firewall de Windows» y abre esa ventana. Pulsa **Cambiar la configuración**, busca en la lista el elemento `dathinel-red`, marca la casilla de la columna **Privada** y pulsa **Aceptar**.
3. Funcionamiento sin permiso: si por algún motivo no se puede dar ese permiso, Rodaje **sigue funcionando a través del servidor intermediario** (un servidor que reenvía los mensajes cifrados), solo que la conexión puede ir algo más lenta.

## 4. Quién hace qué

1. Daniel actúa como el **anfitrión**: tiene el proyecto en su equipo, lo abre, inicia la sala de colaboración y envía la invitación.
2. La otra persona actúa como el **invitado**: recibe el enlace de invitación, se conecta a la sala y colabora desde su ordenador.
3. El proyecto de prueba: Daniel prepara previamente un proyecto pequeño y **lo guarda antes de empezar** (un proyecto sin guardar no se puede compartir). El proyecto debe tener 3 pistas de video, 2 pistas de audio y unos 5 clips.
4. Los archivos de video y audio (medios): en esta versión, Rodaje no transfiere los archivos pesados de video ni de audio por internet. Daniel se los envía al invitado previamente por otro medio, o se utilizan archivos que el invitado ya tenga guardados. Si al invitado le falta algún archivo, el clip aparecerá como «sin conexión»: esto es normal en esta fase, no impide editar la línea de tiempo y debe anotarse.

## 5. Paso a paso, con qué probar y qué se debe ver

| Paso | Qué hacer | Qué debería pasar y qué anotar |
|---|---|---|
| 1. Hospedar el proyecto | Daniel abre el proyecto guardado y entra en **Archivo › Colaborar › Hospedar este proyecto**. Elige caducidad de 1 hora y marca «de un solo uso». Copia el enlace `rodaje://unirse/...` (o enseña el código QR en pantalla) y se lo manda por mensaje a la otra persona. | Rodaje genera el enlace y queda esperando la conexión. Anotar la hora exacta y si el enlace se generó de inmediato. |
| 2. Unirse a la sala | El invitado abre el enlace recibido (o entra en **Archivo › Colaborar › Unirse...** y pega el enlace). Elige una carpeta en su disco para guardar su copia del proyecto. Mientras espera, ve sus **propias cuatro palabras** (las que verá Daniel en su aviso). | La pantalla del invitado muestra sus cuatro palabras y la barra de progreso de la colaboración (indica porcentaje, fase, «último avance hace N s», tiempo restante y botón Cancelar). Si tarda más de un minuto sin cambiar, anotar la hora y el último mensaje. |
| 3. Comprobación por voz | Daniel y el invitado se leen mutuamente en voz alta las **cuatro palabras** que cada uno ve en su pantalla para confirmar que coinciden exactamente. | Las palabras deben ser exactamente iguales en ambas pantallas. Esta comprobación asegura que están conectándose entre sí y no con otra persona. Si no coinciden, se detiene la prueba y se anota. |
| 4. Aceptar la entrada | Daniel ve en su pantalla el aviso «[nombre] quiere entrar» junto a las cuatro palabras confirmadas. Elige la opción **Editar** para conceder permiso de edición. (Más adelante se puede repetir con **Solo mirar**). | El invitado entra al proyecto, se le carga la línea de tiempo y en la barra superior aparecen los nombres y colores asignados a cada uno. |
| 5. Revisar el panel de conexión | Ambos abren el panel de conexión en **Archivo › Colaborar › Estado de la conexión** `[confirmar con la pantalla]`. | Debe indicar si el camino es **directo o por servidor intermediario** (relé), la **latencia** (ida y vuelta, en ms), la **pérdida** de paquetes (%) y la **versión** de cada uno. Anotar los tres números en cada comprobación. |
| 6. Edición simultánea | Cada persona mueve y recorta clips en **pistas distintas** al mismo tiempo (por ejemplo, Daniel en video 1 y el invitado en video 2). | Los cambios del compañero aparecen en pantalla a los pocos segundos. El cabezal de reproducción y la pista que edita la otra persona se ven marcados con su color y su nombre. |
| 7. Intento de tocar la misma pista | Los dos intentan editar clips en **la misma pista** al mismo tiempo. | La segunda persona no puede realizar la acción: el gesto queda bloqueado y sale un aviso indicando que la pista está en uso. Su cambio se deshace; si hizo cambios propios posteriores en otras partes válidas, solo se deshace lo rechazado y lo posterior se reproyecta y conserva (aviso único). Probar a pedir la pista y cederla. |
| 8. Deshacer cambios | Cada uno pulsa **Ctrl+Z** para deshacer su última edición. | La acción solo deshace las ediciones hechas por uno mismo; las modificaciones de la otra persona en sus respectivas pistas permanecen intactas. |
| 9. Corte de conexión a propósito | El invitado apaga su conexión Wi-Fi durante 20 segundos y la vuelve a encender. | Rodaje muestra el mensaje «Sin conexión...». El invitado puede seguir editando en su pista reservada. Al reactivar el Wi-Fi, la conexión **vuelve sola** (reintenta a 1, 2, 4 hasta 30 s) y sincroniza los cambios pendientes. |
| 10. Cambio de red | El invitado cambia su conexión de Wi-Fi a datos móviles (compartiendo internet desde el teléfono) y ambos repiten la revisión del paso 5. | La sala sigue activa. Comprobar en el panel de conexión si el camino cambió de directo a servidor intermediario (relé) o al revés. Anotar los nuevos valores. |
| 11. Conexión solo por relé | En **Preferencias › Colaboración** `[confirmar con la pantalla]`, se activa la opción **«Solo por relé»** y se repiten los pasos 5 y 6. | La conexión pasa siempre por el servidor intermediario, así que la otra persona no ve tu dirección IP. La latencia suele aumentar; anotar el nuevo valor medido. |
| 12. Proxies compartidos (copia ligera) | El invitado sin los archivos originales pide «la copia ligera» al anfitrión para editar y reproducir en su línea de tiempo. Luego intenta exportar desde su equipo. | El invitado puede editar y oír con la copia ligera recibida. Al intentar exportar, la interfaz avisa «Exporta en el anfitrión». Comprobar que a través del relé público de pruebas no se envían proxies. |
| 13. Salida del anfitrión (sin pantalla todavía) | No se prueba con el ratón: el traspaso, el suplente y la lista «Cambios que no entraron» solo tienen motor y pruebas automáticas; las pantallas llegan después. | Anotar solo si Rodaje se cierra o se congela al cerrar el anfitrión a mitad de una edición. |
| 14. Cierre y reapertura | El invitado sale de la sala colaborativa; Daniel cierra la sala en el anfitrión. Daniel guarda el proyecto, cierra Rodaje y vuelve a abrir el archivo en su equipo. | El proyecto se abre con total normalidad y conserva tanto los cambios hechos por Daniel como los realizados por el invitado. |

### 5b. Comprobaciones de seguridad (sin ser técnico)

Se hacen en la misma sesión, antes del paso 14. Detalle y más casos en `docs/pruebas-a-mano.md` («Colaboración: seguridad»).

| Comprobación | Qué hacer | Qué debería pasar |
|---|---|---|
| S1. Sin aprobar no se ve nada | El invitado abre el enlace y **no** se le acepta todavía. | Mientras Daniel no pulse Editar o Solo mirar, el invitado no ve proyecto, medios ni línea de tiempo. Si Daniel pulsa Rechazar, el invitado ve que no entró. |
| S2. Enlace de un solo uso | Una tercera persona (o el mismo invitado desde otra ventana) abre el mismo enlace después de haber entrado. | No entra sola: no ve nada o queda esperando a que Daniel la apruebe. |
| S3. Solo mirar | Daniel cambia al invitado a «Solo mirar» e intenta mover un clip. | El invitado no puede editar, y en el equipo de Daniel no cambia nada. |
| S4. Expulsar corta | Daniel expulsa al invitado. | El invitado pierde la conexión al instante, no recibe más cambios y el mismo enlace no le deja volver a entrar. |
| S5. Sin rutas ajenas | El invitado mira medios, avisos y mensajes. | No aparece ninguna ruta de carpetas del PC de Daniel, solo nombres de medios. |
| S6. Altas simultáneas de medios | Ambos participantes añaden un medio a la biblioteca al mismo tiempo. | Los dos extremos convergen exactamente al mismo estado del proyecto. |
| S7. Invitado sin archivo de medio | Un invitado no tiene el archivo de un medio del anfitrión. | El medio sale sin conexión, pero esto no impide ni bloquea que entren más invitados a la sala. |

## 6. Qué anotar siempre

Copia esta plantilla en un bloc de notas y ve completándola a lo largo de la prueba:

```text
Fecha y hora de inicio:
Ciudad y país de Daniel:
Ciudad y país del invitado:
Tipo de conexión de Daniel (casa / universidad / datos móviles):
Tipo de conexión del invitado (casa / universidad / datos móviles):
Camino inicial (directo / servidor intermediario):
Latencia inicial (ida y vuelta en ms):
Porcentaje de pérdida inicial (%):
Versión mostrada en ambos extremos:
Camino tras cambio de red o ajuste (directo / servidor intermediario):
Latencia tras cambio (ms):
Porcentaje de pérdida tras cambio (%):
Acciones realizadas por cada uno:
Comportamiento esperado frente al observado:
Incidencias o dudas surgidas:
```

## 7. Si algo falla: qué capturar

Si ocurre un fallo, la aplicación deja de responder o un cambio no llega a sincronizarse:

1. Haz una captura de pantalla completa donde se vea claramente el aviso de error o el panel de conexión, asegurándote de que el reloj de Windows muestre la hora.
2. Pulsa el botón **Copiar diagnóstico** `[confirmar con la pantalla]` en el panel de conexión y pega el texto resultante en tus notas. Si no tienes esa opción disponible, lee en voz alta la ficha de diagnóstico explicada en la sección siguiente.
3. Si el ordenador lo permite, comprime y envía la carpeta `registros` ubicada junto al archivo del proyecto.
4. Seguridad: **nunca** envíes claves privadas ni contraseñas. El informe de diagnóstico generado por Rodaje no incluye ningún dato secreto.

## 8. Qué decir por teléfono

Cuando quieras comunicar a la otra persona el estado de tu enlace durante la llamada, abre el panel de diagnóstico y lee los datos línea por línea con este formato:

```text
Camino: por relé (relé de n0)    ← o: directo
Ida y vuelta: 118 ms
Pérdida: 0,4 %
Versión: tuya 1.0.4 · de Luis 1.0.4 (iguales)
Actividad: hace 2 s
Última caída: ninguna            ← o: «Se cortó la conexión de red», hace 3 min
```

Cómo interpretar esta información:
1. Camino: «directo» es el enlace ideal y más rápido; «por relé» (servidor intermediario) es lo normal en redes universitarias, empresas y conexiones con datos móviles.
2. Ida y vuelta (latencia): menos de 100 ms es excelente; entre 100 y 250 ms es adecuado para editar con fluidez; más de 250 ms produce un retraso perceptible al ver las ediciones del otro.
3. Pérdida: menos de 1 % es una conexión limpia; más de 5 % indica una conexión inestable con posibles tirones.
4. Versiones: si no coinciden, la persona con la versión más antigua debe actualizar Rodaje para poder continuar.

Qué esperar de esta prueba en concreto: el servidor intermediario público (el de n0) está en Estados Unidos y Europa, no en Sudamérica. Medido desde Colombia, una edición tarda unos 190 ms en confirmarse (a veces más de 300 ms) y entrar a la sala unos 1,2 s. Entre dos países puede ser algo más. Es normal y no es un fallo: el cambio se ve al instante en tu pantalla y los 190 ms son hasta que el anfitrión lo confirma. Anota las cifras que veas.

## 9. Problemas conocidos y qué hacer

| Situación | Causa habitual | Qué hacer |
|---|---|---|
| No pasa de «esperando aprobación» | El anfitrión todavía no ha aceptado la entrada del invitado. | El anfitrión debe revisar el aviso en su pantalla y pulsar «Editar» o «Solo mirar». |
| El enlace no abre Rodaje al hacer clic | El sistema operativo o el navegador no tienen asociado el protocolo `rodaje://`. | Abre Rodaje a mano, entra en **Archivo › Colaborar › Unirse...** y pega el enlace en el cuadro de texto. |
| El enlace aparece como caducado | Pasó el tiempo de caducidad configurado o ya se utilizó un enlace marcado de un solo uso. | El anfitrión debe generar un nuevo enlace de invitación y enviárselo a la otra persona. |
| Mensaje «Las versiones no coinciden» | Cada equipo tiene una versión diferente de la aplicación. | El mensaje de Rodaje indica qué versión tiene cada parte; la persona con la versión más baja debe actualizar. |
| Una red de universidad o empresa no deja conectar | La red bloquea conexiones directas entre equipos. | Los servidores intermediarios de Rodaje utilizan el puerto 443 (la misma conexión segura que usan las páginas web). Si aun así la red no conecta, prueba compartiendo datos desde el móvil y anota el nombre de la red bloqueada. |
| Windows pregunta por el cortafuegos | Windows detecta por primera vez el componente de red `dathinel-red.exe`. | Pulsa «Permitir acceso» marcando Redes privadas (consulta las instrucciones detalladas en la sección 3). |
| Los medios aparecen como «sin conexión» | Los archivos de video o audio originales no están en la misma ruta en el equipo del invitado. | Es el comportamiento normal en esta versión: Rodaje no transfiere medios por internet. Los archivos se pasan aparte previamente. Se puede seguir editando la línea de tiempo sin problemas. |

## 10. Qué dice Rodaje sobre su privacidad

- Lo que se manda entre los dos equipos va **cifrado de extremo a extremo**: nadie fuera de la sala puede ver el proyecto ni los cambios.
- Cuando la conexión pasa por un servidor intermediario (relé), este solo reenvía mensajes que no puede abrir. Sí ve las direcciones IP de los dos equipos, cuándo hablan y cuántos datos mandan, **nunca el contenido**. Por defecto se usa un relé público de n0 (los creadores de iroh), gratuito y pensado para pruebas: sin garantía de servicio y con límite de tráfico. Por eso esta prueba manda poco: cambios de edición, no videos.
- En conexión directa, cada uno ve la dirección IP del otro. La opción **«Solo por relé»** hace que la otra persona no vea tu IP (el relé sí), a cambio de algo más de retraso.
- La invitación es un enlace con un secreto de un solo uso: no se publica en ningún sitio, caduca y el anfitrión puede anularla cuando quiera. Aun con el enlace, nadie entra sin que el anfitrión lo apruebe viendo sus cuatro palabras.
