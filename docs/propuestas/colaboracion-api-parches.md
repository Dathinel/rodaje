# Colaboración: la API de parches de `Rodaje.Core` (bloque B10)

Contrato para quien programa contra los parches (B11: `CollabSession`, `RoomService`, `MediaMatch`, vistas). Todo vive en `src/Rodaje.Core/Collab/` (espacio de nombres `Rodaje.Core.Collab`), es puro (sin red ni disco, salvo `ContentKey`/`ContentHash`) y no depende de `Rodaje.App`. Diseño: `colaboracion.md` §3.2, §3.3, §4.1, §5 y §9. **Estado:** todo implementado y probado (la prueba de convergencia de §6 incluida). Las firmas son estables; cada cambio se avisa a B11.

## 1. Las piezas

| Tipo | Para qué |
|---|---|
| `ElementKind`, `ElementKey` | Clase e identificador de un elemento compartido: `Media`, `Transcript`, `Sequence` (cabecera), `Track` (sin clips), `Clip`, `Marker`, `Region`. Los identificadores son únicos en todo el proyecto |
| `SharedState` | Fotografía de lo compartido de un proyecto: contenido canónico de cada elemento y orden de las listas. `Capture(project, IBlobMap? = null)` y `Hash` (SHA-256). **Dos réplicas están iguales si y solo si su `Hash` coincide.** No incluye `Project.Name` ni `MediaRef.Path` |
| `Patch`, `PatchOp`, `PatchOrder`, `PatchBlob`, `PatchHeader` | El parche: cambios de elemento (contenido nuevo o `null` = borrar, con la versión de la que partió), orden de listas cuando no sale del resto, y blobs que necesita |
| `PatchDiff.Create(before, after, versions, header)` | De dos `SharedState` a un `Patch?` (`null` si no cambió nada). Determinista |
| `PatchApply.TryApply(project, versions, patch, options?)` | Comprueba versiones, padres y `ModelValidator` del proyecto entero sobre una copia; si pasa, cambia el proyecto y avanza las versiones. Nunca lanza por un parche malo |
| `PatchResult`, `PatchRejection`, `PatchRejectionReason` | `IsAccepted` o el motivo (`Malformed`, `Hostile`, `TooLarge`, `VersionConflict`, `UnknownParent`, `Invalid`) con un texto en español |
| `PatchWire.ToJson` / `TryParse` | El parche como JSON compacto; `TryParse` pasa por `WireSanitizer` y devuelve `PatchParseResult` (parche o rechazo), nunca una excepción |
| `WireSanitizer.Check(json, limits)` / `WireLimits` | Lista de permitidos antes de abrir nada (§5 del diseño): profundidad, tamaño, cantidad, campos desconocidos, identificadores repetidos o mal formados, y ninguna ruta (`file:`, UNC, absoluta, relativa, `..`) |
| `IBlobMap` | Puente que da `Rodaje.App` entre `file:<ruta>` (de cada equipo) y `blob:<sha256>` (de la sala): `Sha256Of(fileReference)` y `FileReferenceFor(sha256)`. Sin él, una referencia `file:` viaja vacía (sin LUT) y nunca como ruta |
| `ProjectWire` | La copia inicial de la sala: `ToJson(project, versions?, blobs?)` y `TryParse(json, blobs?)` → `ProjectParseResult(Project, ElementVersions)` o rechazo. Es un parche de altas sobre un proyecto vacío, con el mismo saneador y las mismas comprobaciones; sin nombre ni rutas. Límite 64 MiB |
| `ElementVersions` | Versión de cada elemento (sin entrada = 0). `Advance(patch)`, `Clone()`, `ToJson()` / `TryParse` para el que entra a mitad de sesión |
| `PatchReservations.Required(patch, project)` | Qué reservas pide el parche: `pista:<id>` por pista tocada y `proyecto` para lo global; añadir un medio nuevo no pide ninguna |
| `CollabHistory` | Deshacer y rehacer propios con parches inversos (se niegan con `ChangedByOthers` si otra persona cambió después un elemento del paso, reordenó la misma lista o puso algo dentro de una pista o secuencia que el paso crea o borra): `Record(before, after, texto)`, `PlanUndo` / `PlanRedo` (devuelven el parche o `ChangedByOthers`), `CommitUndo` / `CommitRedo` cuando el anfitrión acepta, `DiscardUndo`, `Clear` |
| `ContentKey`, `ContentHash`, `MediaRef.ContentKey/ContentHash` | Identidad del medio sin ruta (§4.1): `<tamaño>:<SHA-256 del primer y último MiB>` y SHA-256 del archivo entero. **Ya implementados** |

## 2. Cómo se usa

**Invitado, un paso propio** (el historial de Rodaje avisa con `UndoStack.Changed`):

1. Al abrir la sala y tras cada parche confirmado, guarda `baseline = SharedState.Capture(project)`.
2. Al cerrar un gesto: `after = SharedState.Capture(project)`; `patch = PatchDiff.Create(baseline, after, projected, header)` donde `projected` es la copia de `confirmed` a la que ya se aplicó `Advance` de los parches pendientes. Si no es `null`: reservas con `PatchReservations.Required`, `PatchWire.ToJson` y a la sala. `projected.Advance(patch)`; `baseline = after`.
3. El anfitrión responde `aceptado` (el parche pasa a `confirmed` con `confirmed.Advance(patch)`) o `rechazado` (se retiran los pendientes en orden inverso, se aplica lo confirmado y se reenvía lo que siga siendo válido).

**Anfitrión y quien recibe parches de otros:** `PatchWire.TryParse(json)` → si no es válido, `rechazado` con el motivo. Si lo es: `PatchApply.TryApply(project, versions, patch)`; aceptado, `versions` ya avanzó y se difunde con su número de orden. El anfitrión descarta los `Patch.Id` repetidos (Core no guarda historia de ids) y comprueba que el autor tenga las reservas que dice `PatchReservations.Required` antes de aplicar. Un invitado que recibe un parche confirmado **lo vuelve a comprobar igual** (un anfitrión malicioso también cuenta).

**Deshacer en sesión:** al confirmarse un paso propio, `history.Record(baseline, after, "Recortar en cadena")`. Deshacer: `outcome = history.PlanUndo(SharedState.Capture(project), projected, header)`; con `Plan`, se manda como un parche cualquiera y, al aceptarse, `history.CommitUndo()`; con `ChangedByOthers`, «No se puede deshacer: alguien cambió ese elemento» y `history.DiscardUndo()`. Sin sesión se sigue usando `UndoStack`, sin cambios.

**Lo que cada réplica conserva de lo local:** `Project.Name` y `MediaRef.Path` no viajan. Un medio nuevo llega con `Path` vacío (sin conexión) hasta que `MediaMatch` lo empareje por `ContentKey` y confirme con `ContentHash`.

## 3. El parche en JSON

```json
{"id":"7f…","base":1842,"author":"k:3f…","desc":"Recortar en cadena",
 "ops":[
  {"kind":"clip","id":"c-91…","parent":"A2","v":17,"content":{ …Clip en el JSON del .rodaje… }},
  {"kind":"clip","id":"c-02…","parent":"A2","v":4,"content":null},
  {"kind":"track","id":"A2","parent":"s-1","v":9,"content":{ …Track sin clips… }}],
 "orders":[{"container":"vtracks:s-1","v":3,"ids":["V1","V2"]}],
 "blobs":[{"sha256":"9F…","size":5242880}]}
```

- Las claves son fijas y el orden de los cambios es el canónico (clase y luego identificador), así que un mismo cambio da el mismo texto. El contenido de cada elemento es el JSON del `.rodaje` de esa versión de Rodaje, **con las claves ordenadas** y **sin rutas**: el medio no lleva `path`; una referencia `file:` de una LUT o una máscara viaja como `blob:<sha256>` y llega como blob (§9).
- **Orden de aplicación** (canónico, no depende del orden del texto): borrados (clip, marcador, región, pista, secuencia, transcripción, medio), altas y cambios (medio, transcripción, secuencia, pista, marcador, región, clip) y, al final, los `orders`. Después, `ModelValidator.Validate` sobre el proyecto entero: si hay un solo error, el parche entero se rechaza.
- Los clips de una pista no llevan orden: salen ordenados por `Start`. Los medios, secuencias, transcripciones, pistas, marcadores y regiones sí, y `orders` solo los lleva cuando los elementos nuevos añadidos al final no dan el orden buscado.
- **Precondición de los `orders`:** cada entrada lleva `v`, la versión del contenedor sobre la que se hizo el orden (`ElementVersions` la sube con cada parche que toca esa lista). Un orden hecho sobre otra versión se rechaza con `VersionConflict`, igual que los `ops`: así reenviar un reordenamiento viejo no puede restaurar un orden anterior.
- **Versiones:** un cambio solo se acepta si `v` es la versión actual del elemento (alta de un identificador nuevo: la que `ElementVersions` tenga para él, 0 si nunca existió). Al aceptar, cada elemento tocado sube a `v + 1`.
- **Qué se mueve y qué no:** un clip puede cambiar de pista; una pista, un marcador o una región **no** cambian de secuencia (el parche se rechaza con `Invalid`). Borrar una pista o una secuencia exige que todo lo que cuelga de ella se borre o se mueva en el mismo parche (con su propia versión): si otra persona puso algo dentro, se rechaza con `VersionConflict` en lugar de llevárselo.
- **Opciones de efectos y transiciones:** ningún valor puede ser una ruta (`file:`, UNC, absoluta, relativa, con `..`), tampoco en opciones que Core no conoce (de paquetes); las únicas referencias a archivos son `blob:<SHA-256 en mayúsculas>`. Los rechazos del validador no repiten el valor que los causó (podría ser una ruta local). Las fechas de las transcripciones viajan siempre en UTC (una fecha sin zona se toma como UTC).
- **Blobs y huella:** `PatchWire.BlobHashes(patch)` lista los `blob:` que el contenido referencia (`Patch.Blobs` queda vacío: Core no conoce los tamaños). Para que una réplica sin el archivo conserve la identidad y la huella, `IBlobMap.FileReferenceFor` debe devolver siempre la ruta de destino del almacén aunque el archivo aún no exista, y `Sha256Of` debe reconocerla por su nombre.
- **Versiones:** el máximo (`int.MaxValue`) no se acepta; `Advance` satura.
- **Límites** (`WireLimits.Default`): 900 KiB por parche (uno mayor viaja como blob con su huella), profundidad 32, 20 000 cambios, identificadores de hasta 128 caracteres, textos de hasta 100 000.

## 4. Compatibilidad con la 1.0.3 y los proyectos sin colaboración

- **Nada cambia si nadie usa la colaboración.** `contentKey` y `contentHash` solo se escriben cuando existen, y solo existen cuando la colaboración (o `MediaMatch`) los calcula. Sin ellos, `ProjectSerializer.MinimumSchemaVersion` sigue dando 13, 14, 15 o 16 por las otras razones de `Format16Uses`, y el archivo sale **byte a byte igual** que en la 1.0.3 (prueba: `ContentIdentityTests.Without_content_identity_nothing_changes_and_the_file_stays_below_16`).
- **La 1.0.3 abre** todo proyecto que no tenga `contentKey`/`contentHash` ni otro campo del 16 (transcripciones, TC y carrete, plantillas). **No abre** un proyecto con alguno de los campos del 16: rechaza el archivo con «creado con una versión más nueva de Rodaje (formato 16)» y no lo toca ni lo pierde. Un proyecto que el invitado guardó con colaboración activa tiene `contentKey`/`contentHash` y, por tanto, es del 16 para la 1.0.3.
- Una sala exige la **misma versión de Rodaje** en todos (el saludo la compara), porque el contenido del parche es el JSON del `.rodaje` de esa versión.

## 5. Lo que vive fuera del `.rodaje` (§9 del diseño)

| Qué | Dónde está | En la fase 1 |
|---|---|---|
| Ruta de cada medio | `MediaRef.Path` | No viaja. Cada equipo empareja por `ContentKey` y confirma con `ContentHash`; sin medio, «sin conexión» |
| Nombre del proyecto | `Project.Name` | No se comparte (es de cada copia) |
| LUT sueltas y máscaras de archivo | opciones `file:` de efectos, transiciones y la máscara de mate | Viajan como **blob** `blob:<sha256>`; el parche las lista en `blobs` |
| Análisis de estabilizar | `.rmot` / `StabilizationStore` | Blob por huella del clip; sin él, el clip sale sin corrección y se avisa |
| Medios generados y máscaras creadas | `GeneratedMedia`, `<proyecto>.medios` | Blob por `ContentHash` del medio |
| Organización de la biblioteca (subclips, multicámaras, carpetas) | `LibraryOrganizationStore` | Local; se avisa. Pasa al proyecto solo si Daniel lo decide |
| Fuentes de los títulos | Sistema o paquetes | No viajan: si falta, aviso y respaldo |
| Paquetes de efectos o transiciones (`paquete@versión/elemento`) | Instalados por el usuario | No viajan: el clip queda con su respaldo y un aviso |

## 6. Pruebas y qué falta

- `PatchConvergenceTests`: 10 000 secuencias aleatorias de ediciones (las de `UndoExactnessPropertiesTests`: línea de tiempo, keyframes, marcadores, regiones, mezclador, compuestos, grupos cerrados y cancelados, deshacer y rehacer, órdenes que fallan) en A; cada paso sale como parche de `PatchDiff`, pasa por `PatchWire` y se aplica en B con `PatchApply`; huella igual al final (y en cada paso en una de cada 25), y otra réplica C con los pares contiguos de parches independientes (clips de pistas distintas) en el otro orden.
- `CollabHistoryTests`: 1 000 secuencias de pasos con deshacer y rehacer de `CollabHistory` dan el mismo estado que el historial de una sola persona, y la réplica B converge; más los casos con otra persona editando.
- `WireSanitizerTests` y `ProjectWireTests`: JSON hostil (profundidad, tamaño, campos desconocidos, ids repetidos, rutas `file:`/UNC/absolutas, ruta en un medio, blobs mal formados, texto roto) se rechaza sin excepciones.
- **Sin hacer en B10:** convertir `file:` ↔ `blob:` con los archivos reales (`IBlobMap` lo implementa `Rodaje.App`); medir el coste de `Capture` en un proyecto de 5 000 clips (hoy recorre todo el proyecto en cada paso; si pasa de 2 ms por paso, se guarda el contenido por pista); el registro persistente de la sala y las reservas (de Sala y B11).
