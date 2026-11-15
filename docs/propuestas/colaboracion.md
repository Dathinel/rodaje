# Colaboración en Rodaje: una sala con anfitrión, como los juegos en LAN

Propuesta del 15-nov-2026 para después de la 1.0.3. Solo diseño: nada de esto está en el código y no se escribe hasta que Daniel lo apruebe. Base leída: `main` en `e0bb60bc`. Decisión propuesta: **D-067** (D-060 a D-066 quedan para los siete diseños de este lote, que hoy se anuncian todos como D-060).

**Parte de Unicomp, no repite su investigación.** Unicomp ya tiene aprobado como propuesta el mismo modelo (D-017, `Unicomp\docs\diseno-equipo-animacion-plataformas.md` §8.6): sala en la instancia del PC más potente, enlace o QR, aprobación a mano, **iroh** como transporte, copia completa en cada miembro, traspaso de anfitrión, alcance limitado al proyecto y doce huecos con su mitigación. Aquí se adapta a un editor de video (medios de gigas, proxies, render del anfitrión, la línea de tiempo) y se separa **la capa común de red y sala** de lo propio de cada programa.

Lo que dijo Daniel (15-nov): una app de Rodaje hace de anfitrión y las otras se conectan; reconoce que es «una brecha de seguridad a lo Tailscale» pero quiere limitar el alcance; y la conexión va **dentro de la app**, sin tutoriales de Tailscale.

## 0. Resumen

| # | Decisión | Por qué, en una línea |
|---|---|---|
| 1 | **Sala con anfitrión que ordena**; cada invitado edita en local y manda el resultado; el anfitrión acepta en orden y difunde | Como Figma y como Unicomp; sin CRDT y sin servidor propio |
| 2 | **Se sincroniza el estado, no la orden:** un **parche por elemento** (clip, pista, medio, cabecera de secuencia) con la versión de la que partió, sacado de comparar antes y después de cada paso del historial | **Corrige lo conversado:** de las órdenes, 147 fábricas de `Edits` son serializables, pero hay **46 `new SequenceEditCommand(…, ctx => …)` con una lambda** (44 en `Rodaje.App`), que no se pueden mandar por red. El parche cubre todas sin tocarlas y no exige determinismo |
| 3 | **Reserva por pista** (suave, automática al empezar un gesto, se suelta tras 2 min quieto), no fusión | Una pista es la unidad natural de reparto en video («tú la música, yo el diálogo»); el clip es demasiado fino para un recorte en cadena |
| 4 | **Deshacer de cada uno con parches inversos**, no con `UndoStack` | `SequenceSnapshot` restaura la secuencia entera: deshacer en un invitado borraría lo que llegó de otros (§3.3) |
| 5 | **Medios:** 1) cada uno con su copia, emparejada por **contenido** (muestra de 2 MiB para buscar y SHA-256 entero para confirmar, sin ruta ni fecha); 2) el anfitrión manda **proxies** por esa huella. El visor compuesto a lo Parsec, no | 1 no mueve gigas; 2 deja editar a quien no tiene el original; 3 es otro producto |
| 6 | **El anfitrión renderiza:** exporta para todos y su caché de render (D-060 propuesto) se comparte en la red local | Es «el PC más potente»; el caché ya va por huella y no por ruta |
| 7 | **Transporte iroh**, como Unicomp, en **un proceso de Rust común** (`dathinel-red`) que habla el **protocolo de módulos v1** sin cambios y se distribuye como paquete **`modulo-red`** del catálogo firmado | Ni Tailscale (cuenta por persona o servidor, runtime de Go) ni ZeroTier (BSL) ni ICE propio; la tubería y el catálogo ya existen |
| 8 | **Capa común en un repositorio aparte** (`Dathinel/sala`, provisional) con el protocolo de módulos extraído (lo que D-031 previó «cuando llegue el segundo programa»), el cliente de sala en C# y el proceso de Rust; submódulo en Rodaje y en Unicomp | Unicomp es el segundo programa; un submódulo es lo que ya se usa (`externo/rodaje`) |
| 9 | **Fase 1 = red local**, medios propios, parches sincronizados; internet y proxies después | Lo arriesgado (deshacer con otros, reservas, parches) se prueba sin NAT ni relés |

## 1. Experiencia de usuario

- **Invitar** (menú Archivo › Colaborar › «Hospedar este proyecto»): el proyecto tiene que estar guardado. Sale un panel con el **enlace** `rodaje://unirse/<billete>` y su QR, caducidad (24 h por defecto, o 1 h) y «de un solo uso» marcado por defecto. El billete lleva el `EndpointId` del anfitrión (clave pública), su relé y un secreto de sala de 256 bits; en la fase 1 no lleva relé y solo sirve en la red local.
- **Unirse** (Archivo › Colaborar › «Unirse…», o abrir el enlace): pide una carpeta donde guardar la copia, enseña la **huella de cuatro palabras** del anfitrión y espera. En el anfitrión: «**Luis** quiere entrar · *río-mesa-luna-ocho* · Editar / Solo mirar / Rechazar». Aceptado, la clave de Luis queda anclada y vuelve sin aprobación.
- **Al entrar** recibe el `.rodaje` (sin rutas, §4.2) y la lista de medios por huella. Rodaje busca cada medio en las carpetas que elija (el reenlazado de hoy, `MediaRelinkSearch`, por huella de contenido); lo que no encuentra queda **sin conexión** y, desde la fase 4, con «Pedir proxy al anfitrión».
- **Quién está y qué toca:** fila de avatares en la barra de título; en la línea de tiempo, el cabezal de cada uno con su color y la **pista reservada** con una franja del color y su alias («Ana edita A2»). Intentar editar una pista ajena: el gesto no empieza y sale el aviso; se puede pedir («Pedir A2 a Ana»).
- **Caída de red:** «Sin conexión con el anfitrión: tus cambios se guardan aquí». Se sigue editando en las pistas que se tenían reservadas (las demás pasan a solo lectura). Lo que se guarda: cada miembro lleva junto al proyecto un **registro** (`<proyecto>.sala\registro.jsonl`) con el último número de orden confirmado, el estado confirmado y los parches pendientes con su identificador; se escribe antes de mandar, así un apagón tampoco pierde nada. Si mientras tanto la reserva caducó y otro la tomó, esos parches van a «Cambios que no entraron». Reintentos 1, 2, 4… hasta 30 s. Al volver, se mandan los parches pendientes; si alguno choca (otro tocó lo mismo), el diálogo «Cambios que no entraron» deja quedarse con lo propio (como clip nuevo en una pista nueva) o descartarlo.
- **Si el anfitrión se va:** «Pasar anfitrión a Ana» con él conectado. Si se apaga de golpe, el **suplente** marcado (por defecto, el que más lleva) ve «Tomar el anfitrión»; los demás lo encuentran por su clave sin enlace nuevo. Cada traspaso sube una **época**; si un corte deja dos anfitriones, al reencontrarse manda la época mayor (a igualdad, la clave menor) y los parches confirmados por el otro desde la separación pasan a «Cambios que no entraron» de sus autores: Rodaje no tiene la unión semántica de Unicomp, así que no se promete unir solo. Lo de exportar y el caché compartido se van con el anfitrión hasta que el nuevo lo haga.
- **Exportar:** un invitado ve «Exportar en el anfitrión» además de «Exportar aquí». El anfitrión acepta (como acepta a las personas) y el archivo queda en su disco; mandarlo de vuelta es opcional y solo en red local.

## 2. Qué es común y qué es de cada programa

| Capa común (repositorio aparte, §7) | Rodaje | Unicomp |
|---|---|---|
| Proceso `dathinel-red` (Rust, iroh fijado, ALPN `dathinel/sala/1`, mDNS opcional, relé configurable, inventario de crates) | El parche por elemento (§3.2), su validación con `ModelValidator` | Las operaciones sobre `Uid` validadas por `CircuitViews` (su §8.6.3) |
| Cliente de sala en C# (`Dathinel.Sala`): billetes, aprobación, claves ancladas con huella de cuatro palabras, roles mirar/editar, expulsar y revocar, **orden central** de cargas opacas, **reservas por clave de texto** (`pista:A2`, `seccion:cad/tapa`), presencia, épocas y traspaso, reconexión, **blobs por huella** | Clave de reserva = pista; presencia = cabezal y selección | Clave = sección; presencia = cursor 3D |
| Límites de tamaño y ritmo, pruebas hostiles del marco y de la sala | Pruebas hostiles del parche (rutas, `file:`, generadores) | Las suyas (mallas, código) |
| Documento de seguridad común: los doce huecos de Unicomp §8.6.6 pasan a él tal cual | §5: lo que añade Rodaje | Lo que añada |

**Lo que se mueve del diseño de Unicomp a la capa común:** §8.6.1 (elección de iroh y relés), §8.6.2 (la sala entera), §8.6.4 salvo la unión semántica de E3 (que es suya), §8.6.5 primer y segundo punto (protocolo sin archivos ni comandos, límites), la tabla de §8.6.6, y las rondas E5.1, E5.2 y la parte de transporte de E5.5. El proceso `unicomp-p2p` pasa a ser `dathinel-red`. Queda en Unicomp: E5.3 (sus operaciones), su unión y E5.4 en lo que toca a la unión.

## 3. Arquitectura en las capas de Rodaje

### 3.1 Dónde va cada cosa

| Capa | Archivos nuevos | Qué hace |
|---|---|---|
| `Rodaje.Core` | `Collab/ProjectPatch.cs`, `Collab/PatchDiff.cs`, `Collab/PatchApply.cs`, `Collab/ElementVersions.cs` | Comparar dos estados del proyecto y sacar el parche; aplicarlo sobre una copia candidata comprobando versiones y validando el proyecto entero; puro, sin red |
| `Rodaje.Core` | `Collab/CollabHistory.cs` | Deshacer y rehacer por parches inversos (§3.3) |
| `Rodaje.Core` | `Model/MediaRef.cs`: campos `ContentKey` y `ContentHash` | Identidad del medio sin ruta (§4.1); formato del proyecto sube a v16 (juntarlo con los otros diseños que también lo suben, §9) |
| `Rodaje.Core` | `Collab/WireSanitizer.cs` | Quita y rehace rutas en lo que entra y sale, reutilizando `ProjectFilePaths.RewriteFileOptions` |
| `Rodaje.App` | `Services/Collab/CollabSession.cs` (escucha `UndoStack.Changed`), `RoomService.cs`, `MediaMatch.cs` | Engancha cada paso del historial a la sala; reservas antes del gesto; medios por huella |
| `Rodaje.App` | `Views/Collab/` (panel Invitar, Unirse, aprobar, avatares, franjas de reserva) | Interfaz; la lógica en `Views/Collab/Logic` |
| Común | `externo/sala/` (submódulo): `Dathinel.Sala` y `Rodaje.Modules.Protocol` (que deja `src/`) | §2 |

**Dónde se engancha: en el historial, no en `Execute`.** `EditorSession.Execute`/`TryExecute` (`EditorSession.cs:224` y `:246`) no basta: hay 18 llamadas directas a `History.Execute/Undo/Redo/BeginGroup` en `Rodaje.App` (importar medios, `EditorSession.Files.cs:275`) y «Guardar como» cambia `Project.Name` sin orden (`EditorSession.Files.cs:486`). `CollabSession` escucha `UndoStack.Changed` (salta en ejecutar, deshacer, rehacer, saltos y cierre de grupo) y compara el **proyecto entero** antes y después: medios, lista de secuencias, cabecera, buses, efectos maestros, marcadores, regiones, pistas, clips y transiciones. El nombre del proyecto no se comparte (es de cada copia). Las pistas que siguen siendo el mismo objeto se saltan; las demás se comparan por el JSON de cada elemento. Así el parche sale sin tocar las 147 fábricas ni las 44 lambdas. Prueba de arquitectura: ninguna asignación al modelo fuera de `Rodaje.Core.Commands` y de la carga del proyecto.

### 3.2 El parche

```json
{"base":1842,"autor":"k:3f…","desc":"Recortar en cadena",
 "elementos":[
  {"tipo":"clip","id":"c-91…","pista":"A2","v":17,"nuevo":{ …Clip en el JSON del .rodaje… }},
  {"tipo":"clip","id":"c-02…","pista":"A2","v":4,"nuevo":null},
  {"tipo":"pista","id":"A2","v":9,"orden":["c-91…","c-55…"]},
  {"tipo":"medio","id":"m-7…","v":0,"nuevo":{"name":"Toma 3","contentKey":"52428800:9F…","duration":…}}]}
```

- Cada parche lleva un **identificador propio** (GUID; el anfitrión descarta repetidos, así reenviar tras una caída es seguro) y cada elemento la **versión de la que partió** (`v`). El anfitrión lo aplica sobre una **copia candidata** y lo acepta entero o lo rechaza entero: todas las versiones coinciden, el autor tiene la reserva de cada pista tocada y `ModelValidator.Validate(Project)` (el del proyecto entero, que comprueba medios e identificadores únicos; `ValidateSequence` no basta) da cero errores. Si pasa, sube las versiones, le da número de orden y lo difunde.
- **Lo global** (cabecera y formato de la secuencia, buses, mezcla maestra, crear o borrar secuencias, quitar medios) pide la reserva `proyecto`, que solo tiene uno a la vez. Añadir un medio no la pide: no choca con nada y el anfitrión comprueba que el identificador sea nuevo.
- Los identificadores siguen siendo GUID generados en el invitado (`GuidIdGenerator`); un identificador repetido o mal formado se rechaza como cualquier entrada hostil.
- **Límite de marco:** el parche cabe con su envoltorio en el marco de 1 MiB del protocolo de módulos: el cuerpo se limita a 900 KiB y uno mayor (un recorte en cadena con miles de clips) viaja como blob con su huella.
- **No hace falta determinismo:** nadie vuelve a ejecutar la orden; todos aplican el mismo estado. Lo que sí se exige es misma versión de Rodaje (el saludo la compara) porque el JSON del clip es el del `.rodaje` de esa versión.
- **Predicción local:** el invitado ya tiene su edición aplicada (la hizo en local). Si el anfitrión rechaza un parche y hay otros pendientes detrás, se retiran todos los pendientes en orden inverso, se aplica lo confirmado y se vuelven a mandar los que siguen siendo válidos; el rechazado sale con el porqué. Como la pista estaba reservada, el rechazo solo pasa por una validación que falla en el anfitrión, por un recorte en cadena que toca pistas ajenas, o por una caída.
- **Recortes en cadena entre pistas** (borrar en cadena con «todas las pistas»): tocan pistas que reservan otros; se rechaza con «A3 la tiene Luis» antes de mandarlo (el invitado sabe qué está reservado).

### 3.3 Deshacer con otros

`UndoStack` guarda instantáneas de la secuencia entera (`SequenceEditCommand`: «instantáneas en vez de operaciones inversas»). Con otros editando, deshacer restauraría también sus pistas al estado viejo y, peor, lo mandaría como cambio legítimo. En sesión, `CollabSession` usa `CollabHistory`: cada paso propio guarda su parche y su inverso; deshacer aplica el inverso **solo si el contenido de esos elementos sigue siendo el que dejó el paso** (se compara el JSON del elemento, no su versión: con versiones, editar un clip dos veces y deshacer la segunda dejaría imposible deshacer la primera); deshacer y rehacer son parches nuevos que se mandan como cualquier otro. Si alguien los tocó después, «No se puede deshacer: Ana cambió ese clip» y el paso se descarta, como en Figma. Sin sesión, todo sigue con `UndoStack`: cero cambio para el uso de una sola persona.

### 3.4 El proceso de red y su protocolo

- `dathinel-red.exe` (Rust, iroh 1.x fijado; 0.35 deja de tener relés públicos el 31-dic-2026) se abre como cualquier módulo con `ModuleManager` y habla el **protocolo de módulos v1 sin cambios** (`docs/protocolo-modulos.md`): funciones `abrir-sala`, `unirse`, `aceptar`, `rechazar`, `expulsar`, `enviar`, `recibir`, `blob-pedir`, `blob-dar`, `cerrar`.
- **Sin mensajes nuevos:** lo que llega de la red se lee con una llamada larga `recibir`, que devuelve el siguiente lote en cuanto hay algo (o vacío a los 25 s). La tubería local añade menos de 1 ms. Reglas de servicio: una sola `recibir` a la vez, lotes que caben en 1 MiB (si no, se pagina), el proceso responde `ping` y `cancel` mientras espera, y un cupo de las 4 llamadas concurrentes queda para `enviar` y otro para blobs; si la cola de salida a la red se llena, `enviar` devuelve `busy` y la app reintenta (contrapresión).
- **Blobs** (proxies, fragmentos de caché, el `.rodaje` inicial): el marco de 1 MiB no los lleva. La app elige la ruta de destino dentro de su carpeta de colaboración y se la da al proceso con la huella esperada; el proceso escribe ahí y la app comprueba tamaño y SHA-256 antes de usarlo, como `FileDownloader` hace con las descargas.
- Mensajes de sala entre equipos (dentro de QUIC, ALPN `dathinel/sala/1`): marco de 4 bytes + JSON como el de módulos; tipos `hola` (versión del protocolo y de la app), `bienvenida` (época, miembros, base), `parche`, `aceptado`, `rechazado`, `reserva`, `presencia`, `blob`, `expulsado`, `traspaso`. Payload del programa opaco para la capa común.

## 4. Medios, proxies y render

### 4.1 Emparejar por contenido

Hoy no hay una huella útil entre equipos: `SourceFingerprint.Key` (proxies) mete la **ruta absoluta y la fecha** (`SourceFingerprint.cs`), y `MediaRelinkSearch.Fingerprint` es tamaño + SHA-256 de los primeros bytes, calculada al buscar, no guardada. Se añade `MediaRef.ContentKey` = tamaño + SHA-256 del primer y último MiB (lee 2 MiB, como los proxies), calculada al importar y al abrir proyectos viejos en segundo plano. Es una **muestra**: sirve para encontrar candidatos rápido, no garantiza que el centro del archivo sea igual. Por eso cada medio lleva además `ContentHash` (SHA-256 del archivo entero, calculado en segundo plano, ~1 GB/s) y un medio solo cuenta como «el mismo» en otro equipo cuando coincide el hash entero. El reenlazado busca primero por `ContentKey` y confirma con `ContentHash`. **Para el render en caché (D-060 propuesto)** conviene usar `ContentHash` en la receta: su diseño dice «fecha, tamaño y 2 MiB, nunca la ruta», pero `SourceFingerprint.Key` sí lleva ruta, y con fecha un medio copiado o bajado de la nube no coincide entre equipos.

### 4.2 Proxies del anfitrión (fase 4)

- El invitado pide el proxy de un medio sin conexión; el anfitrión lo genera si no lo tiene y lo manda como blob. **El proxy de hoy no sirve tal cual:** no lleva audio (`-an`, `ProxyEncoders.cs:109`; el audio sale del original) y la limpieza de `ProxyStore` borra los proxies cuyo original falta. Hace falta un **proxy de sala** (variante de `ProxySettings` con AAC estéreo, otra `Tag`), guardado en un almacén aparte por `ContentHash` que la limpieza no toca mientras el proyecto de la sala exista, y que el motor de reproducción acepte un medio «solo proxy» para video **y** audio.
- El medio sigue «sin original» en el invitado: reproduce y edita con el proxy; **exportar desde ahí no se ofrece** para esos medios: solo «Exportar en el anfitrión».
- **Cifras:** proxy por defecto 540p a 3 Mb/s a 30 fps (`ProxySettings.BitrateKbpsAt30Fps`; se escala con la tasa hasta ×2 a 60 fps) + AAC 128 kb/s ≈ 23,5 MB por minuto, **~1,4 GB por hora de material a 30 fps y ~2,8 GB a 60 fps**. Con 10 Mb/s de subida del anfitrión, el mínimo teórico es 19 min por hora de material a 30 fps (38 min a 60 fps) y por invitado, sin contar generarlo. En la misma red, Wi-Fi 5 real ~200 Mb/s: 1 min. Por relé público de n0 **no se mandan proxies** (tiene límite de tráfico y es para pruebas): solo directo o por relé propio.

### 4.3 Render y caché del anfitrión

- **Exportar en el anfitrión:** el invitado manda la petición con los ajustes; el anfitrión la pone en su cola de exportación (con su aprobación) sobre su copia, que es la verdad.
- **Caché compartido:** los fragmentos del render en caché van por huella de receta; si la receta usa `ContentKey`, un fragmento del anfitrión vale para el invitado que tiene los originales. Pesan (ProRes 422 HQ, ~220 Mb/s en 1080p30): **solo en red local**.

## 5. Seguridad, en el lenguaje de `SECURITY_MODEL.md`

**Punto de entrada nuevo:** «pares de una sala de colaboración (red), solo con la sala abierta». **Límite de confianza nuevo (6):** miembro de la sala → anfitrión, y anfitrión → miembro. **Activos:** 1 (que no se ejecute código ajeno) y 3 (proyectos y medios). **Atacantes:** quien consigue el enlace; un miembro malicioso o con el equipo comprometido; la red y el operador del relé; otro proceso del mismo equipo contra la tubería del proceso de red (ya cubierto: `CurrentUserOnly`, nombre con GUID). Por la decisión de Daniel sobre módulos, `modulo-red` es de confianza una vez instalado desde el catálogo firmado.

**Controles** (los doce de Unicomp §8.6.6 van a la capa común; estos son los que añade o concreta Rodaje):

| Amenaza | Control |
|---|---|
| Nada abierto por defecto | El proceso de red solo arranca al hospedar o unirse; se cierra al cerrar la sala. Fase 1: solo direcciones de la red local y sin relé |
| Enlace filtrado | Un solo uso por defecto, 24 h, aprobación a mano, revocar cambia el secreto |
| **Rutas en un parche** (`MediaRef.Path`, referencias `file:` de LUT, barrido por luminancia y máscara de video, `ProjectFilePaths`) | Una **representación de red propia sin campo de ruta**: el medio viaja con `ContentKey`/`ContentHash` y nombre; una referencia `file:` viaja como `blob:<sha256>` y llega como blob. `WireSanitizer` es una **lista de permitidos** que corre antes de resolver o abrir nada: rechaza el parche entero si encuentra `file:`, una ruta UNC, absoluta o relativa, o una opción desconocida con texto (no reutiliza `RewriteFileOptions`, que es privado, deja la referencia si no puede resolverla y solo recorre las opciones que conoce; y la validación de `file:` de hoy admite rutas de red, `EffectDefinition.cs:107`). Sin esto, un `\\equipo\recurso\x.cube` haría que Windows se conecte por SMB al atacante y entregue el hash NTLM |
| Datos dañados (JSON, títulos, curvas, máscaras) | Lector del `.rodaje` (ya fuzzeado y con esquema) con límites: parche ≤ 1 MiB, proyecto inicial ≤ 64 MiB, 20 parches/s por miembro; `ModelValidator` antes de aplicar en el anfitrión **y** en cada invitado (un anfitrión malicioso también cuenta) |
| Ejecutar algo | El protocolo no tiene orden de leer archivos, listar, abrir módulos ni instalar paquetes. Un clip que usa un paquete (`paquete@versión/elemento`) que el invitado no tiene queda con su respaldo y un aviso; instalarlo es del usuario |
| Blobs | Solo los pide el que los necesita, por huella; el que manda no elige ruta; se comprueba SHA-256 y tamaño; un blob solo puede ser un medio del proyecto (proxy o fragmento), y se abre con el lector de medios de siempre (punto de entrada «abrir video» que ya existe) |
| Roles | Mirar: no puede mandar parches ni reservas (el anfitrión los rechaza aunque el cliente los mande). Editar: solo en sus reservas. Expulsar y revocar |
| Versiones distintas | El `hola` lleva versión del protocolo y de Rodaje; distinta, no entra y dice cuál falta |
| Lo que sí queda expuesto | La IP pública a los pares en conexión directa (opción «Solo por relé»); metadatos al operador del relé; el **nombre y la duración de los medios** a los miembros (son parte del proyecto) |

**Pruebas hostiles** como `ProtocolHostileEdgeTests`: parches con rutas absolutas, UNC, `..`, `file:` hacia fuera, versiones falsas, reservas ajenas, tamaños al límite y por encima, ritmo, JSON truncado, un rol de mirar que manda parches, un blob con otra huella. Criterio: ninguno tumba al anfitrión, toca disco fuera de la carpeta de colaboración ni cambia el proyecto.

## 6. Conexión: la comparación, en corto

Unicomp ya comparó a fondo (sus fuentes [G14]–[G19]); se confirma con lo que cambia en Rodaje:

| Opción | Licencia y estado | Cuentas o servidor | Para Rodaje |
|---|---|---|---|
| **Tailscale** embebido (`tsnet`, `libtailscale`; `tailscale-rs` en vista previa desde abril de 2026) | BSD-3 | Una cuenta por persona en su plano de control, o headscale autoalojado; compartir entre cuentas distintas exige compartir el nodo desde la consola | Da una **red IP entera**: justo lo que Daniel quiere evitar (con ella un invitado ve el SMB del anfitrión). La única ventaja para video (montar la carpeta de medios) es la brecha. **No** |
| ZeroTier `libzt` | BSL 1.1 | Su controlador | No cabe en GPLv3. **No** |
| ICE/STUN/TURN propio + QUIC de .NET | — | Un TURN propio | Lo más caro y lo que más falla en NAT difíciles. **No** |
| **iroh 1.x** (n0) | MIT o Apache-2.0; 1.0 publicada en junio de 2026 [I1] | Ninguna; relés públicos de n0 de respaldo; relé propio opcional | Una conexión con un protocolo propio, sin red IP. **Sí** |

**Redes difíciles:** dos casas detrás de CGNAT suelen perforar (el mapeo suele no depender del destino); NAT simétrica o una red de universidad que bloquea UDP van por relé, y el relé de iroh va por HTTPS en el 443, que casi nunca se bloquea. Las Wi-Fi de universidad suelen **aislar clientes**: dos portátiles en el mismo salón no se ven y el descubrimiento local (mDNS, que en iroh no viene activo por defecto [I2]) falla; ahí la sala usa el relé aunque estén a un metro (**verificar en la fase 3** con la red de la universidad de Daniel).

**Relé propio frente al de n0:** el de n0 basta para parches y presencia (KB por minuto); para proxies no (§4.2). Relé propio = el binario `iroh-relay` (mismo repositorio, misma licencia) en un VPS pequeño. Pregunta para Daniel (§10).

## 7. Cómo compartir la capa común (regla «Código compartido»)

| Opción | Ventajas | Desventajas |
|---|---|---|
| **a) Repositorio propio `Dathinel/sala` con el protocolo de módulos extraído, `Dathinel.Sala` y `dathinel-red`; submódulo `externo/sala` en Rodaje y Unicomp** (recomendada) | Es lo que D-031 dejó escrito para el segundo programa; mismo mecanismo que `externo/rodaje`; cada programa fija un commit; la CI de Rust vive aparte y no entra en la de Rodaje (~11 570 pruebas); el binario se publica como paquete del catálogo desde ese repositorio | Un repositorio más; mover `Rodaje.Modules.Protocol` toca los `.csproj` de seis módulos (`modules/*`) y de los `modulo-*`; hay que decidir su licencia |
| b) Paquete NuGet propio | Versión semántica limpia | Hace falta un feed: GitHub Packages pide token incluso para leer, nuget.org es publicar; el binario de Rust no va en NuGet de forma natural; choca con «entorno propio con hashes fijados» |
| c) Que viva en Rodaje y Unicomp lo tome por su `externo/rodaje` | Cero infraestructura nueva | Unicomp depende del ritmo de Rodaje; la cadena de Rust entra en el build de Rodaje; contradice D-031 |
| d) Formato `.paquete` y protocolo de módulos solos | Ya compartidos | Cubren el proceso y su distribución, no la lógica de sala en C#, que se copiaría a mano |

**Recomendada: a), combinada con d) para distribuir el binario** (`modulo-red` en el catálogo firmado, como los módulos de IA). Antes de crearlo hay que confirmar con Daniel nombre, si va público o privado y la licencia (§10).

## 8. Fases

Cada fase en su rama, un commit por tarea, unida cuando compila sin advertencias y pasa pruebas y CI; revisión de bloque al cerrar la fase 3 y la 6.

| Fase | Qué | Quién | Terminado cuando | Semanas de agente |
|---|---|---|---|---|
| **1a. Parches en Core** | `ContentKey` y `ContentHash` (v16, lector **y escritor**: hoy `ProjectSerializer` escribe v13–v15 a propósito), representación de red, `PatchDiff`, `PatchApply`, versiones, `CollabHistory`, `WireSanitizer`; inventario de lo que vive fuera del `.rodaje` (§9) y qué viaja como blob | Claude (tipos, `CollabHistory`, saneador); ChatGPT/codex (`PatchDiff`/`PatchApply` y sus pruebas) | **Prueba de propiedad:** 10 000 secuencias aleatorias de ediciones (fábricas de `Edits`, lambdas de `EditorSession`, órdenes de proyecto, grupos, gestos cancelados y órdenes que fallan) aplicadas en A; parche a B; la **huella del estado compartido canónico** (saneado, sin rutas ni nombre del proyecto) de A y B igual al mismo número de orden. Deshacer por parches da lo mismo que `UndoStack` con una sola persona | 3 |
| **1b. Proceso de red, solo red local** | `dathinel-red` en Rust: iroh con mDNS, sin relé, ALPN; protocolo de módulos v1 en Rust; `cargo vendor`, inventario de licencias, `cargo audit` | ChatGPT/codex (Rust); Claude revisa | Dos procesos en dos equipos de la misma red se encuentran y cambian 10 000 mensajes; las pruebas hostiles del marco pasan contra el proceso de Rust | 2 |
| **1c. Sala en la red local** | `Dathinel.Sala` (billetes sin relé, aprobación, claves ancladas, roles, orden, reservas, presencia) y en Rodaje `CollabSession`, `RoomService`, `MediaMatch`, `Views/Collab` | Claude (sala y sesión); Gemini/agy (interfaz de Invitar/Unirse y textos); Devin (tablas de mensajes y pruebas de roles) | Tres Rodaje en la misma red (el portátil de Daniel con cable y dos por Wi-Fi 5) editan el proyecto de prueba 30 min con medios propios sin divergir (huella del estado compartido igual cada minuto); latencia de una edición, medida en el invitado desde soltar el ratón hasta el `aceptado`, < 30 ms p95 con 3 miembros editando; `pruebas-a-mano.md` con 12 puntos | 3 |
| **1d. Repositorio común** | Tras la respuesta de Daniel (§10.1): crear `Dathinel/sala`, mover `Rodaje.Modules.Protocol`, `Dathinel.Sala` y `dathinel-red` con su historial; submódulo en Rodaje. Va **después** de probar la red local (D-031 permite compartir, no obliga a extraer antes) y antes de que Unicomp llegue a su E5 | Claude (movimiento); Devin (rutas de `.csproj` en los `modulo-*`) | Las pruebas del protocolo, de la sala y de los seis módulos pasan igual desde el submódulo | 1 |
| **2. Seguridad de la sala** | Pruebas hostiles de §5 en la capa común y en Rodaje | Devin (casos), Claude (los que fallen) | Ninguna entrada hostil tumba al anfitrión ni escribe fuera de la carpeta | 1 |
| **3. Internet** | Relé (n0 por defecto, propio configurable), billetes con relé, «Solo por relé», cortafuegos de Windows explicado | ChatGPT/codex | Casa ↔ datos móviles y casa ↔ universidad conectan y dicen si es directo o por relé; latencia < 250 ms p95 por relé dentro del país; captura de tráfico sin texto legible | 1,5 |
| **4. Proxies del anfitrión** | Proxy de sala con audio, almacén por `ContentHash` fuera de la limpieza, medio «solo proxy» en reproducción (video y audio), «Pedir proxy», exportar solo en el anfitrión | Claude; Devin (pruebas del almacén) | Un invitado sin originales edita y oye con proxies; 1 h de material a 30 fps llega en < 25 min con 10 Mb/s de subida medidos | 2,5 |
| **5. Anfitrión que se va** | Traspaso, suplente, épocas, parches pendientes y «Cambios que no entraron» | Claude | Apagar el anfitrión a mitad: nadie pierde cambios; el suplente toma la sala; al volver el primero, converge | 1,5 |
| **6. Render del anfitrión** | «Exportar en el anfitrión»; caché compartido en red local (si D-060 está unido) | Claude; ChatGPT/codex | Un invitado lanza una exportación que sale en el anfitrión; un fragmento del anfitrión se ve en verde en el invitado con los originales | 1,5 |
| | | | **Total** | **17** (de ellas 3 son de la capa común y Unicomp se ahorra lo mismo en E5.1, E5.2 y parte de E5.5) |

**Fase 1 completa (red local con medios propios y parches sincronizados) = 1a + 1b + 1c: 8 semanas; 1d, una más.**

## 9. Riesgos

- **Los otros diseños del lote también suben el formato a v16** (editar por texto, plantillas de movimiento): una sola subida para todos, o se pisan.
- **Cambios que no pasan por el historial:** si algo edita el modelo sin `UndoStack`, el parche no lo ve y los equipos divergen. Ya hay uno (`Project.Name` en «Guardar como», que se deja fuera de lo compartido); la prueba de arquitectura de §3.1 y la huella cada minuto de la fase 1c destapan los demás.
- **Estado fuera del `.rodaje`:** el análisis de estabilizar (`.rmot`, `StabilizationStore`; sin él el clip sale sin corrección), las máscaras y medios generados (`GeneratedMedia`, `<proyecto>.medios`), las LUT sueltas, y los subclips, multicámaras y organización de la biblioteca (`LibraryOrganizationStore`). **En la fase 1 viajan como blobs** las LUT, máscaras y análisis que use la línea de tiempo; la organización de la biblioteca (incluidos subclips y multicámaras) queda local y se avisa, hasta decidir si pasa al proyecto. Las fuentes de los títulos salen del sistema o de paquetes instalados: si el invitado no la tiene, aviso y respaldo.
- **Gestos largos** (arrastrar un clip manda un paso por movimiento del puntero, `UndoStack._lastSnapshot`): se manda un parche al soltar, no por movimiento; la presencia sí se manda al vuelo (≤ 20/s).
- **Parches grandes:** un recorte en cadena en una pista con 2 000 clips manda 2 000 clips. Medir en 1a; por encima de 900 KiB va como blob.
- **Comparar el proyecto entero en cada paso** cuesta un recorrido por paso, aunque las pistas sin tocar se salten por referencia. Medir en 1a con un proyecto de 5 000 clips: objetivo < 2 ms por paso.
- **iroh y su relé público** pueden cambiar condiciones; mitigado con relé propio y versión fijada.
- **Rust en el entorno:** una cadena más en `.entorno\`, con versión y hash fijados.

## 10. Preguntas para Daniel (se avanza con la recomendada salvo la 1, que es de licencia y publicación)

1. **Repositorio común:** nombre (`Dathinel/sala`, `Dathinel/protocolo`…), público o privado, y licencia. **Recomendado:** `Dathinel/sala`, público, **MIT** (para que un tercero escriba módulos con la biblioteca del protocolo sin la GPL; iroh es MIT/Apache y cabe en las dos). Se para hasta tu respuesta.
2. **Relé propio:** a) **n0 por defecto y relé propio opcional en Preferencias (recomendado)**; b) un relé de Dathinel en un VPS (coste mensual y alguien que lo vigile).
3. **Reserva por pista o por clip:** a) **por pista (recomendado)**; b) por clip (más fino, más avisos en recortes en cadena).
4. **Unirse a un proyecto con medios que no tienes:** a) **sin conexión hasta pedir proxies, en la fase 4 (recomendado)**; b) proxies automáticos al entrar (gasta la subida del anfitrión sin preguntar).
5. **Visor del anfitrión a lo Parsec:** a) **no por ahora (recomendado)**; b) como fase 7.
6. **Unicomp y Rodaje en la misma sala** (Unicomp abre «Editar en Rodaje»): fuera de alcance; ¿lo quieres algún día?

## 11. D-067 (para `docs/decisiones.md`)

**D-067. Colaboración con una sala en el anfitrión, parches por elemento y capa de red común con iroh** (15 de noviembre de 2026; propuesta, pendiente de Daniel; después de la 1.0.3). Una instancia de Rodaje hospeda el proyecto y ordena; los invitados entran con un enlace de un solo uso y caducidad, que el anfitrión aprueba a mano. Se sincroniza el estado por elemento (clip, pista, medio, secuencia) con versión, sacado de cada paso del `UndoStack` comparando el proyecto antes y después, porque 46 ediciones son lambdas que no se pueden serializar; el deshacer en sesión va por parches inversos. Reserva por pista. Medios por contenido (`MediaRef.ContentKey` para buscar y `ContentHash` para confirmar), proxies del anfitrión en una segunda fase, sin rutas en la red. El anfitrión exporta para todos. Transporte iroh en un proceso de Rust (`dathinel-red`, paquete `modulo-red`) que habla el protocolo de módulos v1; la sala y el protocolo de módulos salen a un repositorio común con Unicomp (D-017), como submódulo.

## 12. Revisión de otro modelo (ChatGPT, `gpt-6.1-sol`, lectura, 15-nov)

Encargo: `mesa-agentes\datos\encargos\rodaje-diseno-colaboracion.md`; respuesta en `mesa-agentes\datos\trabajos\codex-rodaje-diseno-colaboracion-20261115-163212\final.txt`. Once hallazgos; comprobados a mano los de más peso (`History.Execute` directo en `EditorSession.Files.cs:275`, `Project.Name` en `:486`, `-an` en `ProxyEncoders.cs:109`).

**Incorporado:** el enganche pasa de `Execute` a `UndoStack.Changed` y compara el proyecto entero (1, 2); deshacer por contenido y no por versión, y retirada de pendientes al rechazar (3); validación del proyecto entero sobre copia candidata y reserva `proyecto` para lo global (4); representación de red sin rutas y saneador como lista de permitidos propia (5); LUT, máscaras y análisis como blobs desde la fase 1 (6); proxy de sala con audio, almacén aparte y exportar solo en el anfitrión, cifras a 60 fps (7); `ContentHash` entero además de la muestra (8); reglas de servicio de `recibir`, límite de 900 KiB y contrapresión (9); registro persistente, identificador idempotente y qué pasa con dos anfitriones (10); huella del estado canónico, escritor v16, condiciones de medida, y la extracción del repositorio común después de la red local (11). Total de 15,5 a 17 semanas.

**Descartado:** nada entero. De (10) no se adopta un registro replicado entre todos ni exclusión fuerte de dos anfitriones: con épocas y «Cambios que no entraron» basta para un grupo pequeño, y un consenso tipo Raft es mucho código para un caso raro.
## Fuentes

- Rodaje (`main`, `e0bb60bc`): `src/Rodaje.Core/Commands/` (`ICommand.cs`, `UndoStack.cs`, `SequenceEditCommand.cs`, `SequenceSnapshot.cs`, `Edits*.cs`: 147 fábricas; 46 `new SequenceEditCommand(` en `src`), `src/Rodaje.App/Services/EditorSession.cs:224,246`, `src/Rodaje.Core/Model/MediaRef.cs`, `ProjectFilePaths.cs`, `ModelValidator.cs`, `IdGenerator.cs`, `src/Rodaje.Core/Editing/MediaRelinkSearch.cs`, `src/Rodaje.Media/Proxies/SourceFingerprint.cs`, `ProxySettings.cs`, `ProxyStore.cs`, `docs/protocolo-modulos.md`, `SECURITY_MODEL.md`, `docs/decisiones.md` (D-031, D-059).
- Unicomp: `docs/diseno-equipo-animacion-plataformas.md` §7–§8.8, §9, §10.3 y fuentes [G5], [G13]–[G19].
- Otros diseños del lote: `render-en-cache.md` (receta por huella), `intercambio.md`, `mac-y-linux.md` (tuberías de módulos por socket Unix).
- [I1] iroh 1.0, «Dial keys, not IPs»: https://iroh.computer/blog/v1
- [I2] iroh, descubrimiento local (mDNS opcional, no viene en el preset de n0): https://docs.iroh.computer/connecting/local-discovery
- [I3] `iroh-relay` 1.0.0: https://docs.rs/crate/iroh-relay/1.0.0

## Decisiones de Daniel (15-nov-2026)

1. Repositorio común **`Dathinel/sala`, público, licencia MIT**, como submódulo en Rodaje y en Unicomp: aprobado.
2. Relé: el público de n0 (iroh) por defecto, con opción de poner uno propio en Preferencias: aprobado.
3. Reserva **por pista** (más la reserva `proyecto`): aprobado.
4. Un invitado sin los medios originales queda sin conexión hasta que él pida los proxies: aprobado.
5. Visor del anfitrión a lo Parsec: **no**.
6. Unicomp y Rodaje en la misma sala: **no**. Son aplicaciones aparte; lo que las une es el diseño común del trabajo en grupo (la capa `sala`) y que Unicomp puede abrir Rodaje directamente («Editar en Rodaje»).

### Requisito de Unicomp para la API de `sala` (15-nov)

Unicomp (D-017, commit a12d149) manda ediciones de `montaje.json` validadas por su anfitrión y reservas por sección. Pide a la capa común: un **canal de mensajes ordenado y fiable**, y un **canal aparte para transferencias grandes** (el proyecto entero al entrar, mallas y `.glb` de piezas). Rodaje necesita lo mismo (parches por elemento y proxies), así que la API de `sala` lleva los dos canales. Avisar a la sesión de Unicomp antes de crear el repositorio para que revise la API.
