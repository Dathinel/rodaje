# Textos de interfaz para colaboración en Rodaje y casos de uso confusos

Este documento define todos los textos de la interfaz de colaboración en tiempo real de Rodaje y recoge los casos de uso donde una persona sin conocimientos técnicos puede tener dudas o cometer errores.

---

## Parte 1: Tabla de textos de la interfaz

Reglas aplicadas:
- Lenguaje en español neutro, con tuteo, directo y sin jerga técnica.
- Sin códigos de error ni tecnicismos (sin menciones a redes, puertos, protocolos ni relés; se usa «la conexión»).
- Los botones usan verbos directos de acción.
- Se explica «anfitrión» la primera vez como «quien hospeda el proyecto».
- Sin emojis ni signos de exclamación innecesarios.
- Los textos variables usan `{0}`, `{1}`, etc., y su significado se documenta en la columna «Cuándo sale».

---

### 1. Menú Archivo › Colaborar

| Clave | Texto | Cuándo sale |
|---|---|---|
| `menu.collab.root` | Colaborar | Nombre del submenú dentro del menú principal Archivo. |
| `menu.collab.invite` | Invitar a otras personas… | Opción para hospedar el proyecto actual e iniciar una sala. |
| `menu.collab.invite.tooltip` | Hospeda este proyecto para que otras personas puedan unirse y editar contigo. | Texto de ayuda breve al pasar el puntero sobre la opción de invitar. |
| `menu.collab.join` | Unirse a una sala… | Opción para entrar en una sala creada por otra persona. |
| `menu.collab.join.tooltip` | Entra a colaborar en un proyecto con un enlace o texto de invitación. | Texto de ayuda breve al pasar el puntero sobre la opción de unirse. |
| `menu.collab.room_panel` | Panel de la sala | Opción para mostrar u ocultar el panel lateral con los datos de la sesión. |
| `menu.collab.room_panel.tooltip` | Mira quiénes están en la sala, el estado de la conexión y las pistas ocupadas. | Texto de ayuda breve para abrir el panel de la sala. |
| `menu.collab.leave` | Salir de la sala | Opción visible solo para invitados cuando están dentro de una sala activa. |
| `menu.collab.leave.tooltip` | Sal de la sala. Tu copia del proyecto se quedará guardada en tu equipo. | Texto de ayuda breve al salir de la sala como invitado. |
| `menu.collab.close_room` | Cerrar la sala para todos | Opción visible solo para el anfitrión (quien hospeda el proyecto). |
| `menu.collab.close_room.tooltip` | Cierra la sala. Los invitados se desconectarán pero conservarán su copia. | Texto de ayuda breve para cerrar la sala completa. |

---

### 2. Diálogo Invitar

| Clave | Texto | Cuándo sale |
|---|---|---|
| `invite.dialog.title` | Invitar a colaborar | Título de la ventana para hospedar el proyecto y crear una invitación. |
| `invite.save_required.title` | Guarda el proyecto antes de invitar | Título del aviso si el proyecto tiene cambios sin guardar o nunca se guardó. |
| `invite.save_required.message` | Para compartir este proyecto con otras personas, primero debes guardarlo en tu equipo. | Explicación cuando el proyecto no está guardado en disco. |
| `invite.save_required.button_save` | Guardar y continuar | Botón principal para guardar el archivo y seguir con la invitación. |
| `invite.save_required.button_cancel` | Volver al proyecto | Botón secundario para cancelar la invitación y volver a editar. |
| `invite.field.host_name.label` | Tu nombre en la sala | Etiqueta del campo donde quien hospeda escribe su nombre o alias. |
| `invite.field.host_name.placeholder` | Escribe cómo te verán los demás | Texto de sugerencia dentro del campo de nombre. |
| `invite.field.host_name.validation_empty` | Escribe tu nombre para continuar. | Aviso de validación si el campo de nombre queda vacío. |
| `invite.field.host_name.validation_too_long` | El nombre no puede tener más de 40 caracteres. | Aviso de validación si el nombre supera el límite máximo permitido. |
| `invite.field.default_role.label` | Permiso para quien entra | Etiqueta del selector de rol inicial para las personas invitadas. |
| `invite.field.default_role.edit` | Puede editar | Opción para permitir que quien entre pueda modificar la línea de tiempo. |
| `invite.field.default_role.view` | Solo puede mirar | Opción para que quien entre solo pueda ver la reproducción y los cambios. |
| `invite.field.default_role.hint` | Podrás cambiar este permiso a cada persona en cualquier momento desde la lista de miembros. | Texto aclaratorio debajo del selector de permiso por defecto. |
| `invite.field.validity.label` | Vigencia de la invitación | Etiqueta para elegir cuánto tiempo durará la invitación antes de vencer. |
| `invite.field.validity.one_hour` | 1 hora | Opción de duración corta para sesiones inmediatas. |
| `invite.field.validity.one_day` | 24 horas | Opción de duración recomendada por defecto. |
| `invite.field.single_use.label` | Solo sirve para una persona | Casilla marcada por defecto para invalidar la invitación tras el primer uso. |
| `invite.field.single_use.hint` | Deja de funcionar en cuanto la primera persona entra a la sala. | Explicación breve de la casilla de un solo uso. |
| `invite.action.create` | Crear invitación | Botón para generar el texto y enlace de la invitación. |
| `invite.action.creating` | Creando invitación… | Texto temporal del botón mientras se prepara la sala. |
| `invite.result.title` | Invitación lista | Título de la pantalla tras generar la invitación con éxito. |
| `invite.result.field_label` | Texto de la invitación | Etiqueta sobre el cuadro de texto que contiene el enlace copiable. |
| `invite.result.copy_button` | Copiar invitación | Botón para colocar el texto completo de la invitación en el portapapeles. |
| `invite.result.copied_toast` | Invitación copiada al portapapeles | Notificación breve que confirma que el texto ya se puede pegar. |
| `invite.result.instructions` | Mándasela por mensaje. La otra persona debe tener la misma versión de Rodaje ({0}). Cuando intente entrar, te preguntaremos si la dejas pasar. | Explicación de tres líneas sobre cómo compartir y usar la invitación. `{0}`: versión actual de Rodaje (por ejemplo 1.0.3). |
| `invite.result.expiry_notice` | Esta invitación caduca en {0}. | Indicador del tiempo restante de validez. `{0}`: tiempo restante legible (por ejemplo 23 horas o 45 minutos). |
| `invite.result.button_done` | Listo | Botón para cerrar la ventana y regresar a la edición en la sala abierta. |
| `invite.status.expired` | Esta invitación caducó. Si quieres invitar a alguien más, crea una nueva. | Aviso cuando el plazo de 1 o 24 horas ha vencido. |
| `invite.action.revoke` | Revocar invitación | Botón para cancelar la invitación activa antes de que caduque. |
| `invite.revoke.dialog.title` | ¿Quieres revocar esta invitación? | Título del diálogo de confirmación para anular la invitación. |
| `invite.revoke.dialog.message` | Nadie más podrá entrar con este enlace. Las personas que ya están en la sala seguirán conectadas. | Explicación de las consecuencias de anular la invitación. |
| `invite.revoke.dialog.confirm` | Revocar invitación | Botón de confirmación para invalidar el enlace inmediatamente. |
| `invite.revoke.dialog.cancel` | Mantener invitación | Botón para cancelar la revocación y dejar la invitación activa. |
| `invite.revoke.success` | La invitación se revocó. Ya nadie puede entrar con ella. | Aviso de confirmación tras revocar la invitación. |

---

### 3. Diálogo Unirse

| Clave | Texto | Cuándo sale |
|---|---|---|
| `join.dialog.title` | Unirse a una sala | Título de la ventana para ingresar a un proyecto colaborativo. |
| `join.field.invite.label` | Enlace o texto de invitación | Etiqueta del campo donde se pega la invitación recibida. |
| `join.field.invite.placeholder` | Pega aquí el enlace rodaje:// o el texto que te enviaron | Texto de sugerencia dentro del campo de invitación. |
| `join.field.invite.paste_button` | Pegar | Botón rápido para pegar el contenido del portapapeles en el campo. |
| `join.field.name.label` | Tu nombre en la sala | Etiqueta del campo para el nombre del invitado. |
| `join.field.name.placeholder` | Escribe cómo te verán los demás | Texto de sugerencia dentro del campo de nombre. |
| `join.field.name.validation_empty` | Escribe tu nombre para continuar. | Aviso si el invitado no ha escrito su nombre. |
| `join.field.name.validation_too_long` | El nombre no puede tener más de 40 caracteres. | Aviso si el nombre supera el límite máximo permitido. |
| `join.field.folder.label` | Carpeta donde guardar tu copia del proyecto | Etiqueta del selector de carpeta para almacenar el archivo local. |
| `join.field.folder.browse_button` | Elegir carpeta… | Botón para abrir el selector de carpetas de Windows. |
| `join.field.folder.hint` | Guardamos una copia en tu equipo para que tus cambios no se pierdan si se corta la conexión. | Explicación debajo del selector de carpeta. |
| `join.action.join` | Unirme | Botón principal para iniciar la conexión a la sala. |
| `join.action.cancel` | Cancelar | Botón para cerrar el diálogo sin conectarse. |
| `join.progress.validating` | Comprobando la invitación… | Primer paso de progreso: verificación del formato del texto o enlace. |
| `join.progress.connecting` | Conectando con la sala… | Segundo paso de progreso: contacto inicial con el equipo del anfitrión. |
| `join.progress.waiting_approval` | Esperando a que el anfitrión te deje entrar… | Tercer paso de progreso: solicitud enviada, en espera de respuesta. |
| `join.progress.check_words.title` | Palabras de comprobación: {0} | Muestra las 4 palabras de seguridad de la sala. `{0}`: las cuatro palabras (por ejemplo río-mesa-luna-ocho). |
| `join.progress.check_words.instruction` | Si quieres, dile estas palabras a quien te invitó para que compruebe que eres tú. | Explicación de las palabras de comprobación mientras espera aprobación. |
| `join.progress.receiving_project` | Recibiendo el proyecto ({0}%)… | Cuarto paso de progreso: descarga de la estructura del proyecto. `{0}`: porcentaje numérico. |
| `join.progress.searching_media` | Buscando los medios en tu equipo… | Quinto paso de progreso: localización de archivos de video y audio locales. |
| `join.progress.ready` | Listo. Entrando a la sala… | Sexto paso de progreso: preparación completa antes de abrir la ventana. |
| `join.error.malformed.status` | La invitación no tiene un formato válido. | Estado cuando el texto pegado no corresponde a una invitación de Rodaje. |
| `join.error.malformed.message` | Revisa que hayas copiado el texto completo, sin cortar palabras ni añadir espacios al principio o al final. | Explicación y solución cuando el formato de la invitación es erróneo. |
| `join.error.expired.status` | Esta invitación ya caducó. | Estado cuando la invitación superó su límite de tiempo de 1 o 24 horas. |
| `join.error.expired.message` | El plazo para entrar con esta invitación terminó. Pídele a quien hospeda el proyecto que te mande una nueva. | Explicación y solución cuando la invitación venció. |
| `join.error.already_used.status` | Esta invitación ya se usó. | Estado cuando una invitación de un solo uso ya fue consumida por otra persona. |
| `join.error.already_used.message` | Esta invitación era para una sola persona y ya fue utilizada. Pide que te envíen otra invitación. | Explicación y solución para invitaciones de un solo uso ya utilizadas. |
| `join.error.revoked.status` | Esta invitación fue cancelada. | Estado cuando el anfitrión revocó la invitación antes de su uso. |
| `join.error.revoked.message` | Quien hospeda el proyecto anuló esta invitación. Pídele un enlace nuevo para poder entrar. | Explicación y solución cuando el anfitrión canceló el enlace. |
| `join.error.wrong_app.status` | Esta invitación es de otra aplicación. | Estado cuando se pega una invitación de otro programa (como Unicomp). |
| `join.error.wrong_app.message` | Este enlace no corresponde a Rodaje. Pide una invitación creada desde el menú Colaborar de Rodaje. | Explicación y solución ante invitaciones de programas incompatibles. |
| `join.error.version_mismatch.status` | Tu versión de Rodaje es distinta a la de la sala. | Estado cuando la versión instalada difiere de la del anfitrión. |
| `join.error.version_mismatch.message` | La sala requiere la versión {0} de Rodaje. Actualiza tu programa desde la página de Rodaje para poder unirte. | Explicación con la versión requerida. `{0}`: versión del anfitrión (por ejemplo 1.0.3). |
| `join.error.rejected.status` | No se aceptó tu solicitud. | Estado cuando el anfitrión pulsa Rechazar en la tarjeta de entrada. |
| `join.error.rejected.message` | Quien hospeda el proyecto decidió no darte acceso a la sala en este momento. | Explicación cuando el anfitrión no aprueba el acceso. |
| `join.error.timeout.status` | Quien hospeda no respondió a tiempo. | Estado si transcurren 10 minutos sin que el anfitrión apruebe o rechace. |
| `join.error.timeout.message` | Pasaron 10 minutos sin respuesta. Comprueba que la otra persona tenga Rodaje abierto y vuelve a intentar. | Explicación y recomendación tras agotar el tiempo de espera. |
| `join.error.host_unreachable.status` | No se pudo conectar con quien hospeda. | Estado si no hay ruta de conexión hasta el anfitrión. |
| `join.error.host_unreachable.message` | Revisa tu conexión a internet. Quien hospeda puede haber cerrado Rodaje o su red puede estar bloqueando la conexión. | Explicación de causas posibles y qué comprobar. |
| `join.error.room_full.status` | La sala está llena. | Estado si la sala ya cuenta con 8 integrantes. |
| `join.error.room_full.message` | La sala alcanzó el límite máximo de 8 personas. Espera a que alguien salga para poder unirte. | Explicación del límite de integrantes. |
| `join.error.room_closed.status` | La sala ya no está abierta. | Estado si el anfitrión cerró la sesión antes de completar el ingreso. |
| `join.error.room_closed.message` | Quien hospedaba el proyecto cerró la sala antes de que pudieras entrar. | Explicación cuando la sala fue finalizada por el anfitrión. |
| `join.error.button_retry` | Reintentar | Botón para volver a intentar la conexión con los mismos datos. |
| `join.error.button_back` | Volver | Botón para regresar al formulario inicial de unión. |

---

### 4. Tarjeta de aprobación del anfitrión

| Clave | Texto | Cuándo sale |
|---|---|---|
| `approval.card.title` | {0} quiere entrar | Encabezado de la tarjeta superior cuando alguien solicita unirse. `{0}`: nombre de la persona que pide entrar. |
| `approval.card.check_words` | Palabras de comprobación: {0} | Muestra las 4 palabras de seguridad para cotejar. `{0}`: palabras de verificación (por ejemplo río-mesa-luna-ocho). |
| `approval.card.warning` | Acepta solo si conoces a esta persona y las palabras de comprobación coinciden con las suyas. | Mensaje de precaución para evitar accesos no deseados. |
| `approval.action.allow_edit` | Dejar entrar para editar | Botón para admitir a la persona con permisos de modificación. |
| `approval.action.allow_view` | Dejar entrar solo para mirar | Botón para admitir a la persona únicamente como espectador. |
| `approval.action.reject` | Rechazar | Botón para denegar el acceso a la sala. |
| `approval.queue.counter` | Solicitud {0} de {1} | Indicador de cola cuando varias personas piden entrar a la vez. `{0}`: solicitud actual, `{1}`: total pendientes. |
| `approval.queue.next` | Ver siguiente | Botón para revisar la siguiente solicitud en espera. |
| `approval.queue.previous` | Ver anterior | Botón para volver a la solicitud previa en espera. |
| `approval.notice.requester_left` | {0} canceló la solicitud o se desconectó. | Aviso si la persona cerró su ventana de unión antes de ser respondida. `{0}`: nombre de la persona. |
| `approval.notice.invite_revoked` | Esta invitación se revocó antes de responder. | Aviso si el enlace utilizado fue cancelado mientras la solicitud estaba pendiente. |

---

### 5. Lista de miembros

| Clave | Texto | Cuándo sale |
|---|---|---|
| `members.row.name_you` | {0} (tú) | Indicador junto al nombre del usuario local en la lista. `{0}`: nombre propio. |
| `members.row.name_host` | {0} (anfitrión: hospeda el proyecto) | Indicador junto al nombre de quien inició y aloja la sala. `{0}`: nombre del anfitrión. |
| `members.row.role_edit` | Puede editar | Etiqueta de rol para integrantes con permiso de modificación. |
| `members.row.role_view` | Solo puede mirar | Etiqueta de rol para integrantes en modo lectura/reproducción. |
| `members.action.change_to_view` | Cambiar a solo mirar | Opción del menú de miembro para restringir sus permisos a solo lectura. |
| `members.action.change_to_edit` | Cambiar a puede editar | Opción del menú de miembro para permitirle modificar pistas. |
| `members.action.kick` | Expulsar de la sala… | Opción del menú de miembro para desconectarlo de la sesión. |
| `members.kick_dialog.title` | ¿Quieres expulsar a {0} de la sala? | Título del diálogo de confirmación de expulsión. `{0}`: nombre de la persona a expulsar. |
| `members.kick_dialog.message` | Esta persona se desconectará de inmediato, pero conservará la copia del proyecto que tiene en su equipo. | Explicación de lo que ocurrirá al expulsar a un participante. |
| `members.kick_dialog.checkbox_block_invite` | No dejar que vuelva a entrar con la misma invitación | Casilla marcada por defecto para invalidar la invitación usada por esa persona. |
| `members.kick_dialog.button_confirm` | Expulsar | Botón para hacer efectiva la expulsión del integrante. |
| `members.kick_dialog.button_cancel` | Mantener en la sala | Botón para cancelar la expulsión y cerrar el diálogo. |
| `members.notify.joined` | {0} entró a la sala. | Aviso emergente cuando un participante es admitido. `{0}`: nombre de la persona. |
| `members.notify.left` | {0} salió de la sala. | Aviso emergente cuando un participante se desconecta voluntariamente. `{0}`: nombre de la persona. |
| `members.notify.disconnected` | {0} se desconectó. | Aviso emergente cuando un participante pierde la conexión. `{0}`: nombre de la persona. |
| `members.notify.kicked` | {0} fue expulsado/a de la sala. | Aviso emergente cuando alguien es removido por el anfitrión. `{0}`: nombre de la persona. |
| `members.notify.role_changed_view` | Ahora solo puedes mirar en esta sala. | Aviso cuando el anfitrión cambia el rol del usuario local a solo lectura. |
| `members.notify.role_changed_edit` | Ahora puedes editar en esta sala. | Aviso cuando el anfitrión otorga permisos de edición al usuario local. |

---

### 6. Pistas reservadas

| Clave | Texto | Cuándo sale |
|---|---|---|
| `tracks.reserved.badge` | {0} edita aquí | Etiqueta visible en la cabecera de la pista cuando alguien trabaja en ella. `{0}`: nombre de quien edita. |
| `tracks.reserved.conflict.title` | Pista ocupada | Título del aviso al intentar editar una pista reservada por otra persona. |
| `tracks.reserved.conflict.message` | {0} tiene esta pista ahora. Puedes esperar a que termine o pedírsela. | Explicación al interactuar con una pista en uso ajeno. `{0}`: nombre de quien tiene la pista. |
| `tracks.reserved.conflict.button_request` | Pedir pista a {0} | Botón para enviar una solicitud formal de cesión de pista. `{0}`: nombre de la persona. |
| `tracks.reserved.conflict.button_dismiss` | Entendido | Botón para descartar el aviso sin solicitar la pista. |
| `tracks.reserved.requested_notice` | Le pediste la pista a {0}. Te avisaremos si te la entrega. | Confirmación en pantalla tras enviar la solicitud de pista. `{0}`: nombre de la persona. |
| `tracks.reserved.incoming_request` | {0} necesita editar en esta pista. ¿Quieres liberarla? | Pregunta que recibe quien tiene la pista cuando otro se la pide. `{0}`: nombre de quien la pide. |
| `tracks.reserved.incoming_release` | Liberar pista | Botón para soltar la pista inmediatamente y cedérsela al compañero. |
| `tracks.reserved.incoming_keep` | Seguir editando | Botón para mantener la reserva y continuar trabajando en la pista. |
| `tracks.view_only.warning.title` | Permiso de solo lectura | Título del aviso si alguien con rol de mirar intenta modificar la línea de tiempo. |
| `tracks.view_only.warning.message` | Solo puedes mirar en esta sala. Pídele a {0} que te deje editar. | Explicación al intentar editar sin permisos. `{0}`: nombre del anfitrión. |
| `tracks.released_inactivity.toast` | Se liberó la pista {0} porque {1} no hizo cambios en 2 minutos. | Notificación cuando una pista queda disponible por tiempo sin actividad. `{0}`: nombre de la pista (ej. Video 1), `{1}`: nombre del usuario anterior. |

---

### 7. Medios

| Clave | Texto | Cuándo sale |
|---|---|---|
| `media.search_dialog.title` | Busca tus archivos | Título de la ventana para vincular los archivos de video y audio en el equipo invitado. |
| `media.search_dialog.description` | Para editar sin esperas, Rodaje usa los archivos de video y audio que tengas en tu equipo. Los cambios del proyecto se comparten, pero los archivos no viajan por internet. | Explicación inicial para el invitado al abrir un proyecto compartido. |
| `media.status.found` | Encontrado | Estado junto a un medio cuando el archivo fue localizado en el disco local. |
| `media.status.searching` | Buscando… | Estado mientras Rodaje escanea las carpetas en busca del contenido. |
| `media.status.missing` | No está en tu equipo | Estado cuando el medio no se encuentra en las carpetas examinadas. |
| `media.action.choose_folders` | Elegir carpetas donde buscar… | Botón para indicar en qué carpetas del disco buscar los videos y audios. |
| `media.summary.count` | Encontré {0} de {1} medios. | Resumen numérico del rastreo de archivos. `{0}`: cantidad encontrados, `{1}`: total de medios del proyecto. |
| `media.action.continue` | Continuar al proyecto | Botón para cerrar la búsqueda y pasar a la ventana principal de edición. |
| `media.missing.badge` | Sin conexión | Etiqueta visible sobre los clips en la biblioteca y la línea de tiempo cuyo archivo falta. |
| `media.missing.tooltip` | Este archivo no está en tu equipo. Pídele a {0} el original o espera a que se pueda pedir una copia ligera. | Texto de ayuda sobre un clip sin conexión. `{0}`: nombre del anfitrión. |
| `media.missing.playback_warning` | Las ediciones sobre estos clips se ven en la línea de tiempo, pero no se pueden reproducir hasta tener el archivo. | Banner informativo permanente sobre el reproductor si faltan medios. |

---

### 8. Panel de estado de la sala

| Clave | Texto | Cuándo sale |
|---|---|---|
| `room_panel.project.name` | Proyecto: {0} | Muestra el nombre del proyecto compartido. `{0}`: nombre del proyecto. |
| `room_panel.members.count` | En la sala: {0} personas (máximo 8) | Indicador del número de participantes actuales. `{0}`: cantidad de personas. |
| `room_panel.connection.direct.label` | Conexión directa | Estado cuando los dos equipos se comunican directamente sin servidores intermediarios. |
| `room_panel.connection.direct.desc` | Los equipos se comunican entre sí sin intermediarios. | Explicación en una frase de la conexión directa. |
| `room_panel.connection.relay.label` | A través de un servidor intermedio | Estado cuando la red de uno de los integrantes exige paso por servidor auxiliar. |
| `room_panel.connection.relay.desc` | Un servidor ayuda a conectar los equipos porque la red de uno de ellos no permite la conexión directa. | Explicación en una frase del uso de un servidor intermedio. |
| `room_panel.connection.disconnected.label` | Sin conexión | Estado cuando se pierde la comunicación con la sala. |
| `room_panel.connection.disconnected.desc` | No hay comunicación con la sala. Revisa tu conexión a internet. | Explicación del estado sin conexión. |
| `room_panel.latency.value` | Respuesta: {0} ms | Tiempo de ida y vuelta de la señal. `{0}`: milisegundos de respuesta. |
| `room_panel.version.item` | Versión: {0} | Muestra la versión de Rodaje de cada integrante. `{0}`: número de versión (por ejemplo 1.0.3). |
| `room_panel.version.mismatch_warning` | {0} tiene la versión {1}. Para evitar errores, conviene que todos usen la misma versión ({2}). | Aviso si alguien utiliza una versión diferente. `{0}`: nombre de la persona, `{1}`: su versión, `{2}`: versión de la sala. |
| `room_panel.disconnect.last_time` | Última desconexión: hace {0} | Registro de estabilidad de la sesión. `{0}`: tiempo transcurrido (por ejemplo 15 minutos). |
| `room_panel.disconnect.none` | Sin caídas durante esta sesión | Indicador cuando no se han registrado pérdidas de señal. |
| `room_panel.diagnostics.button` | Copiar diagnóstico | Botón para extraer la información técnica de la conexión de forma segura. |
| `room_panel.diagnostics.explanation` | Copia un resumen sin datos personales para leérselo por teléfono a quien te ayuda. | Explicación del propósito del botón de diagnóstico. |
| `room_panel.diagnostics.copied_toast` | Diagnóstico copiado al portapapeles. | Confirmación tras copiar el diagnóstico de red. |

---

### 9. Caída de la conexión

| Clave | Texto | Cuándo sale |
|---|---|---|
| `connection_loss.banner.lost` | Perdiste la conexión con {0}. Tus cambios se guardan aquí y se enviarán al volver. | Barra superior persistente al cortarse la comunicación. `{0}`: nombre de quien hospeda el proyecto. |
| `connection_loss.banner.retrying_countdown` | Reintentando en {0} s… | Cuenta regresiva del reintento automático periódico. `{0}`: segundos restantes para reintentar. |
| `connection_loss.banner.retrying_now` | Reintentando conectar ahora… | Estado intermedio durante el intento de reconexión. |
| `connection_loss.banner.restored` | Se restableció la conexión. Tus cambios pendientes se enviaron a la sala. | Notificación de éxito al recuperar la comunicación y sincronizar. |
| `connection_loss.notice.host_closed` | {0} cerró Rodaje. La sala terminó, pero tu copia del proyecto sigue guardada en tu equipo. | Diálogo si quien hospedaba el proyecto cerró el programa. `{0}`: nombre del anfitrión. |
| `connection_loss.notice.kicked` | Te expulsaron de la sala. Tu copia del proyecto sigue guardada en tu equipo. | Diálogo si el anfitrión expulsó al usuario local de la sesión. |
| `connection_loss.notice.room_closed` | {0} cerró la sala para todos. Tu copia del proyecto sigue guardada en tu equipo. | Diálogo si el anfitrión finalizó la sala formalmente. `{0}`: nombre del anfitrión. |
| `connection_loss.crash_recovery.title` | Sesión de colaboración interrumpida | Título de la ventana de recuperación si Rodaje se cerró inesperadamente durante la sesión. |
| `connection_loss.crash_recovery.message` | Rodaje se cerró de forma inesperada mientras colaborabas en {0}. Tu copia local se recuperó con todos tus cambios hasta el último momento. | Explicación detallada de la recuperación tras fallo del sistema. `{0}`: nombre del proyecto. |
| `connection_loss.crash_recovery.button_open` | Abrir copia recuperada | Botón para cargar el proyecto recuperado como archivo independiente. |
| `connection_loss.crash_recovery.button_rejoin` | Intentar volver a la sala | Botón para abrir el proyecto e intentar reconectar a la sala si sigue activa. |

---

### 10. Salir y cerrar

| Clave | Texto | Cuándo sale |
|---|---|---|
| `leave_dialog.title` | ¿Quieres salir de la sala? | Título del diálogo cuando un invitado pulsa «Salir de la sala». |
| `leave_dialog.message` | Dejarás de ver los cambios de los demás. Tu copia del proyecto se quedará en tu equipo como un proyecto normal e independiente. | Explicación de las consecuencias de abandonar la sala como invitado. |
| `leave_dialog.button_confirm` | Salir de la sala | Botón de acción para desconectarse y guardar el estado actual. |
| `leave_dialog.button_cancel` | Seguir en la sala | Botón para descartar y continuar colaborando. |
| `close_room_dialog.title` | ¿Quieres cerrar la sala para todos? | Título del diálogo cuando el anfitrión pulsa «Cerrar la sala para todos». |
| `close_room_dialog.message` | Todos los invitados se desconectarán y no podrán seguir editando juntos, pero cada uno conservará una copia del proyecto en su equipo. | Explicación de las consecuencias de terminar la sala para todo el grupo. |
| `close_room_dialog.button_confirm` | Cerrar la sala para todos | Botón de acción para finalizar la sesión global. |
| `close_room_dialog.button_cancel` | Mantener la sala abierta | Botón para descartar y mantener activa la colaboración. |

---

### 11. Cambios que se rechazaron

| Clave | Texto | Cuándo sale |
|---|---|---|
| `rejected_change.toast` | Un cambio tuyo no se pudo aplicar: {0}. Lo deshicimos para que sigas igual que los demás. | Aviso breve en pantalla cuando el anfitrión rechaza una edición. `{0}`: motivo legible documentado abajo. |
| `rejected_change.reason.no_permission` | No tienes permiso para editar | Motivo insertado en `{0}` si el usuario está en modo de solo mirar. |
| `rejected_change.reason.track_occupied` | Otra persona tiene esa pista | Motivo insertado en `{0}` si otro miembro tenía reservada la pista al recibir la orden. |
| `rejected_change.reason.too_fast` | Estás yendo demasiado rápido, espera un momento | Motivo insertado en `{0}` si se envían demasiadas órdenes seguidas que saturan la conexión. |
| `rejected_change.reason.timeout` | El anfitrión tardó demasiado en responder | Motivo insertado en `{0}` si la confirmación de la orden no llegó a tiempo por lentitud de red. |
| `rejected_change.reason.invalid` | El cambio no es válido | Motivo insertado en `{0}` si la combinación de clips resultante generaba un estado incoherente. |
| `rejected_change.conflict_dialog.title` | Cambios que no entraron al proyecto | Título de la ventana cuando hay ediciones locales que chocaron con las de otros tras una desconexión. |
| `rejected_change.conflict_dialog.message` | Mientras no había conexión, otra persona editó la misma parte. Tus cambios no se pudieron combinar con los suyos. | Explicación del conflicto de versiones en desconexión. |
| `rejected_change.conflict_dialog.option_keep_new_track` | Guardar mis cambios en una pista nueva | Opción para no perder el trabajo propio, colocándolo en una pista separada. |
| `rejected_change.conflict_dialog.option_discard` | Descartar mis cambios y quedarme con la versión de la sala | Opción para desechar la edición local conflictiva y adoptar la versión común. |
| `rejected_change.conflict_dialog.button_apply` | Aplicar decisión | Botón para ejecutar la opción elegida en el diálogo de conflictos. |

---

## Parte 2: Casos de uso confusos

Situaciones ordenadas de **más a menos probable** en las que una persona sin conocimientos técnicos puede tener dudas o cometer errores al colaborar en Rodaje.

---

### Caso 1: Pegar la invitación con espacios en blanco o saltos de línea al copiarla del chat
- **Qué ve la persona:** Pega el texto recibido por WhatsApp, Teams o correo en el campo de invitación y teme que dé error, o ve que el botón «Unirme» permanece apagado.
- **Qué espera:** Espera que al pulsar «Pegar» el programa entienda la invitación de inmediato, sin importar si copió espacios antes o después.
- **Qué debería pasar:** El campo de texto limpia automáticamente (`trim`) los espacios iniciales, finales y saltos de línea invisibles. Si aun así falta texto, resalta el campo con una sugerencia clara.
- **Qué texto la ayuda:**
  - *Estado:* «La invitación no tiene un formato válido.»
  - *Explicación:* «Revisa que hayas copiado el texto completo, sin cortar palabras ni añadir espacios al principio o al final.»

---

### Caso 2: El invitado no tiene los videos en su equipo y ve la pantalla en negro o clips en gris («Sin conexión»)
- **Qué ve la persona:** Entra a la sala con éxito, ve los bloques de colores en la línea de tiempo, pero al pulsar reproducir la pantalla se queda en negro o los clips muestran una franja «Sin conexión».
- **Qué espera:** Espera que al entrar a colaborar los videos se hayan descargado solos por internet desde la computadora de quien hospeda.
- **Qué debería pasar:** Al terminar de entrar, se abre automáticamente la pantalla «Busca tus archivos». Si no tiene los videos, se le explica claramente que los archivos grandes no viajan por la red y que debe pedírselos a quien hospeda por su cuenta (o esperar copias ligeras).
- **Qué texto la ayuda:**
  - *Diálogo inicial:* «Para editar sin esperas, Rodaje usa los archivos de video y audio que tengas en tu equipo. Los cambios del proyecto se comparten, pero los archivos no viajan por internet.»
  - *Aviso en el reproductor:* «Las ediciones sobre estos clips se ven en la línea de tiempo, pero no se pueden reproducir hasta tener el archivo.»
  - *Etiqueta sobre el clip:* «Sin conexión. Este archivo no está en tu equipo. Pídele a {0} el original o espera a que se pueda pedir una copia ligera.»

---

### Caso 3: Intentar editar una pista que otra persona tiene reservada en ese instante
- **Qué ve la persona:** Intenta recortar o arrastrar un clip en una pista de video y el ratón no responde o el clip vuelve a su lugar original de inmediato.
- **Qué espera:** Espera poder mover el clip con libertad, o piensa que el programa se trabó.
- **Qué debería pasar:** La pista muestra claramente el nombre del compañero («Ana edita aquí»). Al hacer clic, sale un aviso directo que explica quién la está usando y ofrece un botón para pedírsela amablemente.
- **Qué texto la ayuda:**
  - *Etiqueta de pista:* «{0} edita aquí»
  - *Aviso emergente:* «{0} tiene esta pista ahora. Puedes esperar a que termine o pedírsela.»
  - *Botones:* «Pedir pista a {0}» y «Entendido».

---

### Caso 4: Intentar editar teniendo el rol de «Solo puede mirar»
- **Qué ve la persona:** Hace clic sobre los clips, intenta cortarlos con la tecla S o moverlos, pero nada cambia en la pantalla.
- **Qué espera:** Espera poder hacer cambios en el montaje como cualquier otro editor.
- **Qué debería pasar:** Sale un aviso no intrusivo que le recuerda que quien hospeda le dio permiso únicamente para mirar, y le indica cómo pedir permiso de edición.
- **Qué texto la ayuda:**
  - *Aviso:* «Solo puedes mirar en esta sala. Pídele a {0} que te deje editar.»

---

### Caso 5: Intentar invitar a otras personas con un proyecto nuevo sin guardar
- **Qué ve la persona:** Abre Rodaje, pone dos clips en la línea de tiempo, va a Archivo › Colaborar › «Invitar a otras personas…» y ve un mensaje que le frena el paso.
- **Qué espera:** Espera que la invitación se cree al instante con cualquier proyecto abierto.
- **Qué debería pasar:** Rodaje le explica con amabilidad que para hospedar una sala el proyecto debe existir como archivo en su disco, ofreciéndole un botón directo para guardar y continuar sin perder tiempo.
- **Qué texto la ayuda:**
  - *Título:* «Guarda el proyecto antes de invitar»
  - *Mensaje:* «Para compartir este proyecto con otras personas, primero debes guardarlo en tu equipo.»
  - *Botón:* «Guardar y continuar»

---

### Caso 6: El anfitrión no ve la tarjeta de aprobación porque tiene Rodaje minimizado o tapado
- **Qué ve la persona:** Quien intenta unirse se queda en la pantalla «Esperando a que el anfitrión te deje entrar…», mientras que quien hospeda no ve la tarjeta de aprobación porque tiene Rodaje minimizado o tapado por otra ventana.
- **Qué espera:** Quien se une espera una respuesta rápida o una señal de que la solicitud llegó; quien hospeda espera un aviso claro del sistema si alguien toca a la puerta.
- **Qué debería pasar:** Rodaje hace parpadear su icono en la barra de tareas de Windows y emite un sonido suave de notificación. En la pantalla del invitado, se muestran las 4 palabras de comprobación y se le sugiere avisar al anfitrión por chat o llamada.
- **Qué texto la ayuda:**
  - *En el invitado:* «Si quieres, dile estas palabras a quien te invitó para que compruebe que eres tú: {0}.»
  - *Si pasan 10 minutos:* «Quien hospeda no respondió a tiempo. Pasaron 10 minutos sin respuesta. Comprueba que la otra persona tenga Rodaje abierto y vuelve a intentar.»

---

### Caso 7: Pulsar Deshacer (Ctrl+Z) esperando revertir cambios ajenos o tras un cambio de otro
- **Qué ve la persona:** Pulsa Ctrl+Z y se deshace su propio cambio anterior, o sale un aviso de que no se puede deshacer porque otra persona modificó ese clip después.
- **Qué espera:** Espera que Ctrl+Z funcione como en un procesador de texto tradicional o teme haberle borrado el trabajo a su compañero.
- **Qué debería pasar:** Rodaje solo deshace las acciones propias de cada usuario. Si el clip ya fue alterado por otro miembro de la sala, protege el trabajo común y avisa con claridad.
- **Qué texto la ayuda:**
  - *Aviso:* «Un cambio tuyo no se pudo aplicar: Otra persona tiene esa pista. Lo deshicimos para que sigas igual que los demás.»

---

### Caso 8: Hacer clic en el enlace de invitación `rodaje://unirse/...` con Rodaje ya abierto
- **Qué ve la persona:** Hace clic en el enlace desde su chat o navegador mientras ya tenía Rodaje abierto trabajando en otro proyecto personal.
- **Qué espera:** Espera que Rodaje salte al frente y le pregunte si quiere unirse a la sala sin cerrar a la fuerza lo que estaba haciendo.
- **Qué debería pasar:** Rodaje pasa a primer plano y abre el diálogo «Unirse a una sala» con la invitación ya rellenada. Si el proyecto actual tenía cambios sin guardar, pregunta si desea guardarlo antes de cambiar de proyecto.
- **Qué texto la ayuda:**
  - *Pregunta previa:* «¿Quieres guardar los cambios de este proyecto antes de unirte a la sala?»
  - *Botones:* «Guardar y unirme», «Descartar y unirme», «Cancelar».

---

### Caso 9: Hacer clic en el enlace de invitación con Rodaje completamente cerrado
- **Qué ve la persona:** Pulsa el enlace en el navegador o chat, Windows abre Rodaje y aparece directamente la pantalla para unirse.
- **Qué espera:** Espera no tener que buscar el menú de colaboración ni copiar y pegar códigos a mano.
- **Qué debería pasar:** La aplicación inicia, lee el argumento de la línea de órdenes y presenta la ventana «Unirse a una sala» con el campo de invitación ya completo, lista para pulsar «Unirme».
- **Qué texto la ayuda:**
  - *Ventana de entrada:* «Unirse a una sala. Se preparó la invitación que abriste. Escribe tu nombre para entrar.»

---

### Caso 10: Cerrar por error la ventana mientras espera la aprobación del anfitrión
- **Qué ve la persona:** Pulsa sin querer la X o Cancelar mientras decía «Esperando a que el anfitrión te deje entrar…».
- **Qué espera:** Teme haber roto la sala o no saber cómo volver a solicitar entrada.
- **Qué debería pasar:** En la pantalla del anfitrión la solicitud se retira limpiamente avisando que la persona canceló. El invitado puede volver a pulsar «Unirse a una sala…» y encuentra su invitación y datos anteriores recordados.
- **Qué texto la ayuda:**
  - *En el anfitrión:* «{0} canceló la solicitud o se desconectó.»
  - *En el invitado:* «Cancelaste la solicitud de entrada. Puedes pulsar «Unirme» de nuevo cuando lo desees.»

---

### Caso 11: El chat corta el texto largo de la invitación en dos partes
- **Qué ve la persona:** Quien hospeda le mandó la invitación por un chat que partió el texto largo en dos mensajes. El invitado copia solo el segundo mensaje y sale error.
- **Qué espera:** Espera que cualquier fragmento funcione o no entiende por qué dice que la invitación está rota.
- **Qué debería pasar:** Rodaje detecta si el texto pegado parece un fragmento incompleto e instruye al usuario a copiar el texto entero desde el encabezado `rodaje://`.
- **Qué texto la ayuda:**
  - *Aviso:* «La invitación no tiene un formato válido. Revisa que hayas copiado el texto completo, sin cortar palabras ni añadir espacios al principio o al final.»

---

### Caso 12: Diferencia de versiones de Rodaje entre anfitrión e invitado
- **Qué ve la persona:** Intenta unirse y el proceso se detiene de inmediato con un mensaje de versión incompatible.
- **Qué espera:** Espera que dos versiones parecidas (por ejemplo 1.0.2 y 1.0.3) puedan trabajar juntas sin problemas.
- **Qué debería pasar:** Para evitar que un cambio en la estructura de clips corrompa el proyecto, Rodaje exige la misma versión exacta, indicándole al invitado cuál tiene el anfitrión y dónde conseguirla.
- **Qué texto la ayuda:**
  - *Estado:* «Tu versión de Rodaje es distinta a la de la sala.»
  - *Explicación:* «La sala requiere la versión {0} de Rodaje. Actualiza tu programa desde la página de Rodaje para poder unirte.»

---

### Caso 13: La invitación caducó por tiempo (1 hora o 24 horas transcurridas)
- **Qué ve la persona:** La persona invitada intenta usar el enlace al día siguiente y la conexión falla al comenzar.
- **Qué espera:** Espera que el enlace sea eterno mientras el anfitrión tenga el proyecto abierto.
- **Qué debería pasar:** Rodaje le explica sin rodeos que el plazo de vigencia expiró y que solo hace falta que el anfitrión le mande un enlace nuevo.
- **Qué texto la ayuda:**
  - *Estado:* «Esta invitación ya caducó.»
  - *Explicación:* «El plazo para entrar con esta invitación terminó. Pídele a quien hospeda el proyecto que te mande una nueva.»

---

### Caso 14: Intentar usar una invitación de un solo uso que ya fue utilizada
- **Qué ve la persona:** El anfitrión pasó la misma invitación a dos amigos por un grupo de chat. El primero entra bien; el segundo recibe un error de inmediato.
- **Qué espera:** Esperaban poder entrar los dos con el mismo mensaje copiado.
- **Qué debería pasar:** Se explica que la invitación estaba configurada para una sola persona y que ya fue aprovechada, pidiéndole al anfitrión otra invitación para el segundo amigo.
- **Qué texto la ayuda:**
  - *Estado:* «Esta invitación ya se usó.»
  - *Explicación:* «Esta invitación era para una sola persona y ya fue utilizada. Pide que te envíen otra invitación.»

---

### Caso 15: Dos participantes eligen el mismo nombre en la sala (nombres duplicados)
- **Qué ve la persona:** Una persona entra llamándose «Daniel» cuando quien hospeda también se llama «Daniel». En las pistas dice «Daniel edita aquí» y no se distingue quién es quién.
- **Qué espera:** Cada usuario espera ver claramente qué está haciendo él y qué hace su compañero.
- **Qué debería pasar:** La interfaz siempre añade la etiqueta «(tú)» al usuario local en la lista de miembros y en las pistas, y diferencia al anfitrión con «(anfitrión)». Si dos invitados se llaman igual, Rodaje numera discretamente al segundo («Daniel 2»).
- **Qué texto la ayuda:**
  - *En la lista:* «{0} (tú)» y «{0} (anfitrión: hospeda el proyecto)»
  - *Aviso al entrar si hubo ajuste:* «Ya había alguien con tu nombre en la sala. Te asignamos temporalmente «{0}» para no confundir los cambios.»

---

### Caso 16: Escribir un nombre con símbolos especiales o dejarlo en blanco
- **Qué ve la persona:** Escribe «* El Editor *» o borra el campo de nombre e intenta pulsar el botón de crear o unirse.
- **Qué espera:** Espera que el programa acepte cualquier combinación o no entiende por qué el botón no avanza.
- **Qué debería pasar:** El sistema valida el campo al instante con mensajes breves, impidiendo nombres vacíos o excesivamente largos (máximo 40 caracteres).
- **Qué texto la ayuda:**
  - *Si está vacío:* «Escribe tu nombre para continuar.»
  - *Si es muy largo:* «El nombre no puede tener más de 40 caracteres.»

---

### Caso 17: El anfitrión suspende el equipo o baja la tapa de su portátil durante la sesión
- **Qué ve la persona:** De repente aparece una barra superior en el invitado indicando que se perdió la conexión con el anfitrión, pero el proyecto no se cierra.
- **Qué espera:** El invitado teme que se haya borrado todo su trabajo de la última hora o que el programa se haya colgado.
- **Qué debería pasar:** Rodaje mantiene la copia intacta en la computadora del invitado, guarda cada cambio localmente en un registro de seguridad y comienza una cuenta regresiva para reintentar la conexión cuando el anfitrión vuelva a abrir la tapa.
- **Qué texto la ayuda:**
  - *Barra de aviso:* «Perdiste la conexión con {0}. Tus cambios se guardan aquí y se enviarán al volver. Reintentando en {1} s…»

---

### Caso 18: Corte temporal de conexión a internet durante la edición activa
- **Qué ve la persona:** El rúter se reinicia o el Wi-Fi titubea por 30 segundos. Sale la barra de conexión perdida.
- **Qué espera:** Espera no perder los clips que acaba de mover hace un instante.
- **Qué debería pasar:** El usuario puede seguir editando en la pista que tenía reservada. En cuanto la red vuelve, Rodaje sincroniza automáticamente sin pedir contraseñas ni aprobaciones nuevas.
- **Qué texto la ayuda:**
  - *Al restablecerse:* «Se restableció la conexión. Tus cambios pendientes se enviaron a la sala.»

---

### Caso 19: La reserva de una pista se libera automáticamente tras 2 minutos de inactividad
- **Qué ve la persona:** Estaba editando en la pista de audio 2, se puso a escuchar música en otra ventana o a buscar un archivo en el explorador durante 3 minutos, y al volver ve que su etiqueta de la pista desapareció.
- **Qué espera:** Esperaba que la pista fuera suya para siempre mientras estuviera en la sala.
- **Qué debería pasar:** Para evitar que un participante bloquee a los demás si se va a comer o se distrae, las pistas se liberan solas a los 2 minutos de inactividad. En cuanto la persona vuelve a tocar un clip de esa pista (si nadie la tomó), la reserva se activa otra vez sin fricción.
- **Qué texto la ayuda:**
  - *Aviso:* «Se liberó la pista {0} porque {1} no hizo cambios en 2 minutos.»

---

### Caso 20: El anfitrión pulsa «Cerrar la sala para todos» pensando que solo él salía un momento
- **Qué ve la persona:** Quien hospeda hace clic en «Cerrar la sala para todos» pensando que solo él salía un momento, y los invitados ven un aviso indicando que la sala se cerró.
- **Qué espera:** Quien hospeda espera poder pausar la sesión sin afectar a los demás, y los invitados temen haber perdido el proyecto.
- **Qué debería pasar:** Rodaje pide confirmación obligatoria al anfitrión antes de cerrar la sala grupal, explicándole claramente la diferencia entre desconectarse él y terminar la colaboración de todos, asegurando que todos conservan su copia local.
- **Qué texto la ayuda:**
  - *Título:* «¿Quieres cerrar la sala para todos?»
  - *Mensaje:* «Todos los invitados se desconectarán y no podrán seguir editando juntos, pero cada uno conservará una copia del proyecto en su equipo.»
  - *Botones:* «Cerrar la sala para todos» y «Mantener la sala abierta».

---

### Caso 21: El invitado intenta exportar el proyecto final a MP4 sin tener los videos originales
- **Qué ve la persona:** El invitado tiene rol de editor, termina el montaje y pulsa el botón Exportar. El programa le indica que no puede exportar localmente los clips sin conexión.
- **Qué espera:** Espera poder sacar el archivo MP4 terminado en su propia computadora.
- **Qué debería pasar:** Rodaje le explica que, como los videos originales en alta calidad están en el equipo de quien hospeda, la exportación final debe realizarse en la máquina del anfitrión.
- **Qué texto la ayuda:**
  - *Aviso en Exportar:* «No puedes exportar este proyecto desde tu equipo porque faltan los archivos originales. Pídele a {0} que realice la exportación desde su equipo.»

---

### Caso 22: El anfitrión rechaza la solicitud de entrada por un clic erróneo
- **Qué ve la persona:** Quien hospeda pulsa por error el botón «Rechazar» en la tarjeta de aprobación, y el invitado ve en su pantalla «No se aceptó tu solicitud».
- **Qué espera:** Quien hospeda espera poder rectificar su clic, y el invitado espera poder volver a pedir entrada sin tener que reiniciar Rodaje ni pedir otro código.
- **Qué debería pasar:** El invitado no queda bloqueado de por vida: tiene un botón «Reintentar» para volver a tocar a la puerta de la sala. El anfitrión recibe la solicitud nuevamente y puede aceptarlo.
- **Qué texto la ayuda:**
  - *En el invitado:* «No se aceptó tu solicitud. Quien hospeda el proyecto decidió no darte acceso a la sala en este momento.»
  - *Botón:* «Reintentar»

---

### Caso 23: La sala alcanza el límite máximo de 8 participantes
- **Qué ve la persona:** Intenta unirse con una invitación válida y el proceso se frena con un aviso de cupo completo.
- **Qué espera:** Espera poder entrar a mirar como en una videollamada sin límite aparente.
- **Qué debería pasar:** Rodaje informa con transparencia que la sala alcanzó su capacidad técnica máxima de 8 personas simultáneas, sugiriéndole esperar a que alguien desocupe un lugar.
- **Qué texto la ayuda:**
  - *Estado:* «La sala está llena.»
  - *Explicación:* «La sala alcanzó el límite máximo de 8 personas. Espera a que alguien salga para poder unirte.»

---

### Caso 24: Conexión en redes Wi-Fi públicas o universitarias con aislamiento de clientes
- **Qué ve la persona:** Dos personas están sentadas en la misma mesa de una cafetería o biblioteca universitaria conectadas al mismo Wi-Fi. Intentan colaborar y la conexión tarda o indica «A través de un servidor intermedio».
- **Qué espera:** Esperan que por estar al lado la conexión sea instantánea y directa.
- **Qué debería pasar:** Como muchas redes públicas aíslan los dispositivos para evitar accesos indebidos, Rodaje conmuta automáticamente a un servidor intermedio sin que el usuario tenga que configurar nada. El panel de estado lo aclara en una frase sencilla.
- **Qué texto la ayuda:**
  - *En el panel:* «A través de un servidor intermedio. Un servidor ayuda a conectar los equipos porque la red de uno de ellos no permite la conexión directa.»

---

### Caso 25: Desfase en el reloj del sistema operativo del equipo invitado
- **Qué ve la persona:** La invitación se creó hace 5 minutos con vigencia de 1 hora, pero el invitado tiene el reloj de su Windows atrasado por 2 horas y el sistema marca invitación caducada o incoherente.
- **Qué espera:** Espera poder unirse normalmente porque su amigo le acaba de enviar el enlace.
- **Qué debería pasar:** Si el desfase de tiempo respecto a quien hospeda supera los márgenes de seguridad, Rodaje le explica de manera comprensible que revise la hora de su computadora.
- **Qué texto la ayuda:**
  - *Aviso:* «No pudimos comprobar la validez de la invitación. Revisa que la fecha y la hora de tu equipo estén configuradas correctamente y vuelve a intentar.»

---

### Caso 26: El anfitrión usa «Guardar como…» para renombrar el proyecto durante la sesión
- **Qué ve la persona:** Quien hospeda el proyecto decide usar «Guardar como…» para renombrar el proyecto mientras hay personas conectadas editando en la sala.
- **Qué espera:** Teme que al cambiar el nombre del archivo se caigan los demás integrantes o se rompa la sincronización del trabajo.
- **Qué debería pasar:** La sesión de colaboración permanece activa sin interrumpirse; el cambio de nombre del archivo local solo afecta a la copia del anfitrión en su disco. Los invitados siguen trabajando en la misma sala sin enterarse del renombrado.
- **Qué texto la ayuda:**
  - *Aviso discreto al anfitrión:* «Guardaste una copia con otro nombre. La sala sigue activa y los demás participantes continúan conectados.»

---

### Caso 27: El anfitrión expulsa a un participante pero no marca revocar la invitación
- **Qué ve la persona:** Quien hospeda expulsa a un participante, pero a los dos minutos esa misma persona vuelve a solicitar entrada a la sala.
- **Qué espera:** Quien hospeda espera que al expulsar a alguien no pueda volver a entrar con el mismo enlace.
- **Qué debería pasar:** En el diálogo de expulsión, la casilla «No dejar que vuelva a entrar con la misma invitación» viene marcada por defecto. Si el anfitrión la desmarcó por error y la persona vuelve a pedir entrada, la tarjeta de aprobación permite rechazarlo de nuevo o revocar la invitación desde el menú.
- **Qué texto la ayuda:**
  - *En el diálogo de expulsar:* «No dejar que vuelva a entrar con la misma invitación (recomendado).»

---

### Caso 28: El proyecto utiliza tipografías, transiciones o complementos que el invitado no tiene
- **Qué ve la persona:** Abre el proyecto compartido y observa que los títulos de texto se ven con una tipografía estándar distinta a la del anfitrión, o un clip muestra un aviso de complemento no instalado.
- **Qué espera:** Espera ver el video con la tipografía y los efectos exactos del anfitrión sin tener que configurar nada.
- **Qué debería pasar:** Rodaje muestra una tipografía de respaldo segura para no romper la edición y avisa amablemente en el inspector qué fuente o complemento falta en su sistema para que pueda instalarlo si lo desea.
- **Qué texto la ayuda:**
  - *Aviso en el inspector:* «Este clip usa una tipografía que no tienes instalada ({0}). Mostramos una alternativa para que puedas seguir editando.» (`{0}`: nombre de la fuente).
