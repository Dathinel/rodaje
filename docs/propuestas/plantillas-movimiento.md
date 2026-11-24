# Plantillas de movimiento: diseño

Bloque aprobado por Daniel el 15-nov para después de la 1.0.3. Diseño sobre `main` a 15-nov (`77f2bd1c`), leyendo el código: no se escribió código ni se ejecutó la app. Decisión propuesta: **D-064** (si otro bloque del mismo día la toma antes, la siguiente libre; el número se fija al unir).

## 1. Qué resuelve

Hoy un rótulo inferior (nombre + cargo con su barra) se arma a mano: dos títulos, sus estilos, sus entradas desfasadas y, si se repite diez veces en un documental, se copia y se edita diez veces. Un estilo (`rodaje.textstyle`) cubre **una** capa; un efecto (`rodaje.effect`) y una transición (`rodaje.preset` v2) traen valores fijos. Lo que falta:

- **Varias capas que se mueven juntas** (texto, segunda línea, fondo) guardadas como una sola pieza.
- **Parámetros expuestos**: quien edita cambia «Nombre», «Cargo», «Color de acento», «Fuente», «Duración» en el inspector, sin ver keyframes ni capas.
- **Reparto y reutilización**: la misma pieza llega por el catálogo (paquete), se guarda propia y se pasa a otra persona en un archivo.

**Cómo lo resuelven otros (solo ideas de producto):**

| Programa | Idea que tomamos | Lo que no |
|---|---|---|
| Premiere (gráficos esenciales) | El diseñador marca qué propiedades se exponen; el editor solo ve esa lista en un panel. Duración con zona protegida al inicio y al final para que la entrada y la salida no se estiren | Depender de otra app para editar la plantilla por dentro |
| Final Cut (plantillas de Motion) | «Publicar parámetro»: cada control publicado tiene nombre y rango; la miniatura se anima al pasar el ratón en el navegador | Plantillas con código de comportamiento |
| Resolve (títulos de Fusion, macros) | Un grupo de nodos con controles expuestos en el inspector; dentro sigue siendo editable para quien quiere | Abrir el grafo entero para cambiar un color |
| Kdenlive (GPLv3) | Títulos en un XML propio; las plantillas de título reemplazan un texto marcador al insertarlas. Lógica estudiada, sin copiar código | Un solo marcador de texto: ni colores ni duración |
| Shotcut (GPLv3) | Preajustes de filtros guardados como archivos sueltos en la carpeta de datos, exportables | Sin capas ni parámetros con nombre |
| Olive (GPLv3) | Grupos de nodos con entradas expuestas | — |
| OpenTimelineIO (Apache-2.0) | `GeneratorReference` con `parameters` libres: un generador se describe por tipo + parámetros, que es lo que guardamos en el proyecto | No define plantillas |
| Lottie (MIT) | «Slots»: valores con nombre que se sustituyen en un JSON de animación sin tocar el resto | Formato de animación ajeno a nuestras primitivas |

La idea común: **la plantilla es datos con huecos con nombre**; aplicarla es rellenar los huecos y obtener objetos que el motor ya sabe dibujar.

## 2. Diseño

### 2.1 Regla de fondo: sustituir y leer con lo que ya existe

Una plantilla **no añade nada nuevo que dibujar**: un título de varias capas son varios títulos. Es un documento `rodaje.template` v1 con:

- `kind`: `title` (títulos y rótulos inferiores, varias capas), `transition` o `effect`;
- `body`: el contenido de siempre de ese tipo (capas de título con su `rodaje.textstyle` v2, un `rodaje.preset` v2 o un `rodaje.effect` v1);
- `parameters`: los huecos con nombre y a dónde apuntan.

Instanciar = copiar `body`, escribir cada valor en sus destinos y pasar el resultado por **el lector que ya existe** (`ContentReaders.ReadTextStyle` para el estilo de cada capa, `ReadTransition`, `ReadEffect`; el sobre de las capas lo valida el lector nuevo, ver 2.3). Así toda la validación de rangos que ya hay sigue siendo la frontera de confianza, el render y la exportación no cambian (el motor solo reparte un clip en varias capas de título), y no hay reflexión (rutas sobre `JsonNode`, apto para AOT).

```json
{ "format": "rodaje.template", "version": 1, "name": "Rótulo inferior Barra", "kind": "title",
  "reference": { "width": 1920, "height": 1080 }, "durationSeconds": 5, "minSeconds": 1.5,
  "body": { "layers": [
    { "id": "nombre", "text": "Nombre Apellido", "start": 0, "x": -560, "y": 380, "box": { "widthFraction": 0.5 },
      "style": { "format": "rodaje.textstyle", "version": 2, "name": "n", "font": { "family": "Inter", "weight": 700 }, "size": 64,
                 "background": { "color": "#E63946", "padding": [24, 10] },
                 "in": { "durationSeconds": 0.5, "curves": { "x": [ { "t": 0, "value": -200, "interp": "easeOut" }, { "t": 0.5, "value": 0 } ] } } } },
    { "id": "cargo", "text": "Cargo", "start": 0.2, "x": -560, "y": 450, "style": { "...": "..." } } ] },
  "parameters": [
    { "id": "nombre", "label": "Nombre", "type": "text", "targets": ["layers/nombre/text"] },
    { "id": "cargo", "label": "Cargo", "type": "text", "targets": ["layers/cargo/text"] },
    { "id": "acento", "label": "Color de acento", "type": "color", "targets": ["layers/nombre/style/background/color"] },
    { "id": "fuente", "label": "Fuente", "type": "font", "targets": ["layers/nombre/style/font", "layers/cargo/style/font"] } ] }
```

| Campo | Regla |
|---|---|
| `parameters[].type` | `text` (hasta 10 000 caracteres, las reglas de `generator.text`), `color` (`#RRGGBB[AA]`), `font` (objeto `font` de un estilo), `number` (con `min`, `max` obligatorios y `step`), `bool`, `choice` (hasta 16 `options` con `label` y `value`). La duración no es un parámetro: es la del clip |
| `targets` | Rutas con `/` sobre `body`; un segmento tras `layers` es el `id` de la capa (no un índice, para que reordenar capas no rompa la plantilla). Hasta 16 destinos por parámetro y 32 parámetros. Una ruta que no existe en `body` o cuyo tipo no casa con el del parámetro rechaza el archivo al leerlo, no al aplicarlo |
| `layers` (solo `title`) | De 1 a 16 capas de texto: `text`, `style` (estilo completo), `start` (s desde el inicio, desfase de entrada), `end` (s antes del final, desfase de salida), `x`, `y` (px de `reference`), `box`. La capa de abajo va primera |
| `durationSeconds`, `minSeconds` | Duración al insertarla y la mínima que admite (la suma de entradas, salidas y desfases); hasta 600 s |
| Límites | 256 KB por archivo (un título lleva sus estilos completos); el resto, las reglas comunes de los JSON de contenido |

Sin `rodaje.textstyle` v3 ni campos nuevos en los estilos: lo nuevo es solo el sobre.

### 2.2 En el proyecto (formato 16)

- **Título o rótulo** → **un clip con generador de tipo `template`** (formato 5 ya prevé tipos nuevos de generador: «color sólido y formas llegarán sin cambiar el formato»). Una sola pieza en la pista, que se mueve, divide, copia, pega y deshace con el código de los generadores que ya existe (`ClipGenerator.Clone`, `ContentEquals`, `SequenceSnapshot.ClipStateEquals`, `GeneratorEdges` al dividir, `ClipClipboard`). La primera versión de este diseño usaba un clip compuesto con una secuencia anidada por rótulo; la revisión de Devin mostró que hoy un compuesto no se copia ni se duplica (`ClipClipboard.cs:75`), que dividirlo deja las dos mitades con la misma secuencia (`TrackOps.SplitOff`), que el historial captura una sola secuencia (`SequenceEditCommand`) y que borrar el clip deja la secuencia huérfana. Con un generador no hay secuencia que mantener: el coste pasa al plan del motor, que es acotado (abajo).

```
"generator": { "kind": "template", "text": "",
  "template": { "source": "rodaje-plantillas@1.0.0/barra", "name": "Rótulo inferior Barra", "minSeconds": 1.5,
                "document": "{…rodaje.template tal cual, texto…}", "values": { "nombre": "Ana Ruiz", "acento": "#2A9D8F" },
                "layers": [ { "id": "nombre", "text": "Ana Ruiz", "style": { …estilo completo… }, "box": { "widthFraction": 0.5 },
                              "x": -560, "y": 380, "start": 0, "end": 0 }, … ] },
  "edges": { "in": true, "out": true } }
```

  - `layers` es **el resultado instanciado**, con las reglas de un título por capa (`ModelValidator.Generators` se reutiliza capa a capa) más `x`, `y` (px de la referencia del estilo, escalados con la misma razón `min(ancho/refAncho, alto/refAlto)` que el estilo, para que una plantilla de 1920 × 1080 caiga en su sitio en 4K o en 9:16), `start` y `end` (s de desfase desde cada borde). Es lo que el motor dibuja y lo que Core valida.
  - `document` es la copia del `rodaje.template` como texto (hasta 256 KB), para poder volver a instanciar con otros valores; `values`, los valores puestos. Core no los interpreta (Core no referencia `Rodaje.Packages` ni al revés): solo comprueba tamaño y que `values` sea un objeto plano. Volver a instanciar lo hace la App con `Rodaje.Packages` y entrega a Core las capas nuevas (`TemplateEdits.SetValues(clip, values, layers)`), un paso del historial como cualquier edición de título. Copia completa, como el estilo (D-029): el proyecto se ve igual sin el paquete.
  - «Convertir en títulos» (el equivalente a desvincular) reemplaza el clip por un título por capa en pistas libres; es la salida para quien quiere editar por dentro.
- **Duración.** No hace falta relayout: al armar el plan, cada capa ocupa `[inicio + start, final − end]` del clip (con `edges.before`/`after` si salió de dividir, como un título hoy) y la entrada y salida de su estilo van pegadas a esa ventana. El recorte no baja de `minSeconds` (`TrimmingSupport`: un tope mínimo para generadores `template`; hoy un generador no tiene tope).
- **Efecto** → se aplica como hoy (`EffectCommands.ApplyEffect`: efectos copiados y curvas a keyframes) con el `body` instanciado; **cada** efecto copiado guarda `options.template.values` (JSON compacto), así borrar uno no pierde los valores. Cambiar un valor quita lo que puso esa aplicación (por `options.source`, ya existe) y la vuelve a aplicar: **descarta los retoques manuales sobre lo aplicado**, y el panel lo dice antes.
- **Transición** → `TransitionInstance` de tipo `rodaje.preset` como hoy; los valores van en `Options["template.values"]`. Cambia la firma: `IEffectContent.FindTransition(string reference, string? values)`, enhebrada desde `EffectPlan` (que hoy lee `Options["preset"]`) y con caché por (referencia, valores) en `EffectContent`. Los `mixParameters` instanciados sustituyen a los de la pista, como ya hace `EffectPlan` con un preajuste. El cargador (`IContentLoader.LoadTransition`, `PackageContentStore`) tiene que aceptar un elemento `motion-template` de `kind: transition`. Si falta el paquete, fundido cruzado con aviso, como hoy.
- **Versión del formato: 15 → 16, solo cuando hace falta.** `ProjectSerializer` ya escribe la versión mínima que necesita el archivo (15 con HDR, 14 con imágenes fijas, si no 13): se añade `hayPlantilla ? 16`. Así un proyecto sin plantillas sigue abriendo en la 1.0.x, y uno con plantillas lo rechaza con «actualiza el programa» en lugar de perder las capas (un Rodaje 15 conservaría el generador desconocido sin dibujarlo). Migración 15 → 16 en `ProjectMigrator.DefaultChain`, que solo cambia el número; esquema nuevo `schema/rodaje-proyecto.v16.schema.json`.

### 2.3 Archivos que se tocan o se crean

| Capa | Archivo | Qué |
|---|---|---|
| Packages | `Content/PresetReader.Template.cs` (nuevo), `Content/TemplateTypes.cs` (nuevo) | Lector `rodaje.template` v1: sobre, parámetros y comprobación de rutas; `TemplateInstantiator.Instantiate(document, values)` da capas con su `TextStyleDefinition` (estilo por `ReadTextStyle`), un `TransitionPreset` o un efecto. Las capas de título no tienen lector propio hoy: su sobre (`text`, `box`, `x`, `y`, `start`, `end`) se valida aquí repitiendo los topes del generador (10 000 caracteres, 200 líneas, caja 0–1); los números van en una clase de constantes con una prueba en Core que exige que coincidan con `ModelValidator` |
| Packages | `ManifestReader.cs` | Tipo de elemento `motion-template` (nuevo; `template` ya existe y queda para plantillas de proyecto). Un Rodaje viejo lo omite con aviso, como cualquier tipo desconocido |
| Packages | `PackageWriter.cs` (nuevo) | Escribe un `.paquete` de un elemento con orden y fecha fijos (mismas fuentes → mismos bytes, como los `empaquetar.ps1`) |
| Core | `Model/ClipGenerator.cs` (`GeneratorKinds.Template`, propiedad `Template`, y **`Clone` y `SameContent` que la incluyan**: si no, la copia comparte las capas y el deshacer no ve el cambio), `Model/GeneratorTemplate.cs` (nuevo: `Source`, `Name`, `MinSeconds`, `Document`, `Values`, `Layers`), `Model/Project.cs` (16), `Serialization/ProjectSerializer.cs` (versión mínima), `ProjectMigrator` (15→16), `ModelValidator.Generators.cs` (capas: las reglas del título por capa; documento ≤ 256 KB; 1–16 capas; ids únicos) | Modelo y reglas |
| Core | `Editing/TemplateEdits.cs` (nuevo: insertar, `SetValues`, convertir en títulos), `Editing/TrimmingSupport.cs` (tope `minSeconds`), `Editing/TitleEdits.cs` (lo que hoy exige `Kind: text` y deba aceptar plantillas, por ejemplo el texto en la pista: se revisa caso por caso) | Edición |
| Engine | `Timeline/GeneratedPlan.cs`: `From` devuelve una lista; un generador `template` da un `GeneratedClipPlan` por capa, con su ventana dentro del clip, su `x`/`y` sumado a la transformación del clip y los efectos del clip compartidos. `TimelinePlan.Video` recorre la lista. `IEffectContent.FindTransition` con valores (arriba) | Plan |
| Render | — | Nada: cada capa es un título más (`TitleTexture` y su caché no cambian) |
| App | `Services/MotionTemplates/MotionTemplateCatalog.cs` (nuevo; las de fábrica, las instaladas y las de `propios`; carpeta aparte de `Services/Templates`, que son las plantillas de proyecto), `Services/MotionTemplates/TemplateThumbnails.cs` (nuevo), `Views/Inspector/Template/TemplatePanel.*` (nuevo: la lista de parámetros), biblioteca: categoría «Plantillas» (`LibraryView.Templates.cs`, nuevo), `EffectContent` (transiciones con valores) | Interfaz |
| Docs | `formato-paquete.md` (`motion-template`), `formato-proyecto.md` (formato 16), `guia/` (una página), `decisiones.md` (D-064) | |
| Contenido | Repositorio nuevo `Dathinel/rodaje-plantillas` (CC0): 8 rótulos inferiores, 6 títulos, 4 transiciones y 4 efectos, todos con fuentes de `rodaje-fuentes` | |

**Miniatura animada.** No la trae el paquete: la genera Rodaje desde la plantilla misma (12 cuadros a 192 × 108 con los valores por defecto, por `OfflineTimeline` y el compositor, como un cuadro de exportación) y la guarda en `%LOCALAPPDATA%\Rodaje\cache\plantillas\<sha256 del documento>.png` como tira. La biblioteca la anima al pasar el ratón y muestra el primer cuadro quieto (o el `preview` PNG del manifiesto mientras se genera). Ventajas: nada que confiar de una imagen ajena, siempre igual a lo que se aplica y las propias tienen miniatura gratis. Desventaja: la primera vez cuesta (cifra abajo).

**Guardar, importar y exportar.** «Guardar como plantilla…» en el menú de uno o varios títulos seleccionados (cada uno será una capa, con su posición y su desfase respecto del primero): un diálogo lista los campos de cada capa (texto, color de relleno, de fondo, de borde, fuente, tamaño) con una casilla «Expuesto» y el nombre que verá quien edita. Va a `propios\<nombre>.json` con `"kind": "motion-template"` (el mismo patrón que las zonas propias). «Exportar…» escribe un `.paquete` de un elemento con `PackageWriter`; «Importar» es el instalador local que ya existe (`PackageService.InstallFromFileAsync`).

**Versión y compatibilidad.** `version` del documento: 1; una mayor pide «actualiza el programa» (regla común). Un parámetro de un `type` desconocido en una v1 futura rechaza el archivo (cambia lo que se puede editar). En el proyecto, `template.source` guarda `paquete@versión/elemento`; si hay una versión instalada más nueva, el panel ofrece «Actualizar a 1.1.0» que reaplica la plantilla nueva **conservando los valores cuyo id siga existiendo** (los demás vuelven a su defecto y se dice cuáles). Nunca automático.

### 2.4 Rendimiento (cifras objetivo)

| Medida | RTX 3050 (Daniel) | Sin GPU (WARP, CI) | Cómo se mide |
|---|---|---|---|
| Instanciar y validar una plantilla de 16 capas | ≤ 2 ms | igual (CPU) | `TemplateInstantiatorTests`, 1000 veces, mediana |
| Cambiar un valor con el inspector hasta el cuadro nuevo en el visor (1080p, rótulo de 2 capas) | ≤ 33 ms (un cuadro a 30 fps) | — | autoprueba: paso «plantilla» en `SelfTestRun` con el tiempo en el informe |
| Reproducir un rótulo de 4 capas sobre un clip 1080p60 | 0 cuadros perdidos en 10 s | — | autoprueba: el contador de cuadros perdidos de `PlaybackScheduler`, llevado al informe |
| Componer un cuadro con el rótulo de 4 capas | ≤ 1,5 ms más que sin él | ≤ 25 ms | `TemplateRenderTests` (Render.Tests, WARP en la CI; la cifra de la GPU, larga) |
| Miniatura animada (12 cuadros) | ≤ 150 ms | ≤ 1,5 s, en segundo plano | `TemplateThumbnailTests` |
| Biblioteca con 50 plantillas, primer dibujo | ≤ 100 ms (miniaturas en caché) | igual | medición de la biblioteca que ya existe (`LibraryScrollMeasurement`) |

**Medido (16-nov, a prioridad baja y con otras compilaciones a la vez; la app nunca a la vista).**

| Medida | Resultado | Objetivo | Estado |
|---|---|---|---|
| Instanciar y validar 16 capas (mediana de 1000) | 0,71 ms | ≤ 2 ms | Cumple |
| Guardar y reabrir un proyecto con 500 rótulos | 588 ms | < 1 s (riesgo «Proyecto más grande») | Cumple |
| Cambiar un valor hasta el cuadro nuevo (autoprueba, 1080p, 2 capas) | 47,6, 91,8 y 356,9 ms en tres corridas, como cota superior: son dos cambios seguidos más bajar el cuadro de la GPU para compararlo, con el equipo cargado | ≤ 33 ms | Sin demostrar: falta medir un cambio solo con el equipo libre |
| Cuadros perdidos reproduciendo el rótulo (autoprueba) | 0 en 2 s, 2 capas (tres corridas) | 0 en 10 s, 4 capas sobre 1080p60 | Parcial: escena más corta y ligera |
| Miniatura animada de 12 cuadros | 450 ms con los shaders en caché (`MotionTemplateTests`); de 1964 a 5023 ms la primera, con abrir la GPU y compilar shaders (autoprueba, tres corridas) | ≤ 150 ms (RTX 3050), ≤ 1,5 s (WARP) | No cumple en la GPU; va en segundo plano y se guarda en disco |
| Componer un cuadro con un rótulo de 4 capas | sin medir (`TemplateRenderTests` no se escribió) | ≤ 1,5 ms más | Pendiente |
| Biblioteca con 50 plantillas, primer dibujo | sin medir | ≤ 100 ms | Pendiente |

Autoprueba completa con el paso «plantilla»: 52,9 s, código 0 (sobre el #560). Tras el #563 el exe AOT se publica sin advertencias y el paso «plantilla» pasa; esa corrida se lanzó solo con `--autoprueba` y sin el módulo eco publicado, así que su paso de IA no cuenta y la autoprueba completa por `buildpp.ps1 -Autoprueba` queda por repetir.

La textura de cada título ya se rasteriza una vez y se reutiliza (`TitleTexture`); cambiar un valor solo vuelve a rasterizar las capas cuyo texto o estilo cambió.

### 2.5 Pruebas sin WinUI

Todo lo nuevo en Packages, Core y Engine se prueba en sus proyectos de siempre; la App solo lleva el panel y la biblioteca, con la lógica (`MotionTemplateCatalog`, lista de parámetros, «Guardar como plantilla») en clases sin WinUI como el resto de `Services/`. Lo mínimo:

- **Packages:** cada regla del lector con su prueba; 1000 documentos dañados al azar solo dan `PackageContentException`; instanciar con valores fuera de rango los rechaza el lector del tipo; rutas inexistentes o con tipo cruzado; `PackageWriter` da los mismos bytes dos veces y el instalador lo acepta.
- **Core:** insertar → guardar → abrir da el mismo proyecto; un proyecto sin plantillas se sigue guardando con la versión de antes y uno con plantilla, como 16; migración 15→16; cambiar valores es un paso del historial y deshacer devuelve los bytes de antes; recortar se para en `minSeconds`; dividir en plena entrada deja las dos mitades viéndose igual que antes (como un título); copiar, pegar en otro proyecto y duplicar no comparten capas (mutar la copia no cambia el original); «Convertir en títulos» da el mismo cuadro.
- **Engine/Render:** el cuadro del rótulo instanciado es idéntico (byte a byte, WARP) al de las mismas capas puestas a mano; la transición con valores cambia el ángulo del barrido.
- **Aceptación:** exportar un proyecto con un rótulo y leer con ffprobe que la duración y el cuadro de referencia coinciden.

## 3. Fases

Cada fase en su rama y su worktree, un PR por fase, unión por squash con la CI verde. Las ramas paralelas no se pisan porque tocan carpetas distintas; donde dos fases tocan el mismo archivo se dice.

| Fase | Qué | Quién | Rama | Hecho cuando |
|---|---|---|---|---|
| **0. Formato** | Este documento pasado a `formato-paquete.md` (sección `motion-template`) y `formato-proyecto.md` (formato 16), D-064 en `decisiones.md`, el esquema v16 | Claude escribe; Gemini (agy) revisa la lectura de los dos docs contra este diseño | `plantillas/0-formato` (solo `docs/` y `schema/`) | Docs y esquema unidos; el esquema v16 valida un documento de ejemplo con una prueba del esquema (la ida y vuelta por el serializador es de la fase 2) |
| **1. Lector e instanciador** | `PresetReader.Template.cs`, `TemplateTypes.cs`, `TemplateInstantiator`, tipo `motion-template` en el manifiesto | ChatGPT (codex): código; Gemini: las pruebas de reglas y las 1000 dañadas | `plantillas/1-lector` (solo `src/Rodaje.Packages`, `tests/Rodaje.Packages.Tests`) | Todas las reglas de 2.1 con prueba; mediana ≤ 2 ms; cero advertencias; AOT smoke compila |
| **2. Modelo, edición y plan** | `GeneratorTemplate`, `Clone`/`SameContent`, formato 16 con versión mínima, migración, validador, `TemplateEdits`, tope de recorte; `GeneratedPlan` por capas | Claude: modelo, serializador, migración y validador (lo delicado: integridad del proyecto y deshacer); ChatGPT: `TemplateEdits` y el plan por capas; Devin: pruebas sencillas de ida y vuelta, migración y ejemplo `Serialization/ejemplos/proyecto-v16.rodaje` | `plantillas/2-modelo` (`src/Rodaje.Core`, `tests/Rodaje.Core.Tests`, `src/Rodaje.Engine/Timeline/GeneratedPlan.cs` y `TimelinePlan.Video.cs`, `tests/Rodaje.Engine.Tests`). No depende de 1: Core guarda el documento como texto | Las pruebas de Core de 2.5; el cuadro de un rótulo de dos capas es idéntico (WARP) al de los dos títulos puestos a mano |
| **3. Transiciones y efectos** | `FindTransition(reference, values)`, caché, cargador que acepta `motion-template`; reaplicar efecto con valores | ChatGPT | `plantillas/3-transiciones` (`IEffectContent`, `EffectPlan`, `EffectContent` y `PackageContentStore`). Tras 1; paralela a 2 (no tocan los mismos archivos: 2 toca `GeneratedPlan`, 3 `EffectPlan`) | Prueba del barrido con ángulo expuesto; sin paquete, fundido cruzado y aviso; ninguna reserva por cuadro (prueba nueva con `GC.GetAllocatedBytesForCurrentThread`, como `ClipDragCostTests`) |
| **4. Contenido** | Repo `Dathinel/rodaje-plantillas` (privado hasta que Daniel lo publique), 22 plantillas, `empaquetar.ps1` copiado del de `rodaje-titulos` | Claude: las 4 primeras como modelo; Devin: el resto siguiendo el modelo y el `README`; Gemini: revisión de lectura (rangos, nombres, licencia) | Repo propio. Paralela a 2 y 3 | El paquete se instala con el instalador real en una prueba de Packages (`RODAJE_PRUEBAS_LARGAS`) y todas las plantillas instancian con sus valores por defecto |
| **5. Interfaz** | Biblioteca «Plantillas» con miniatura animada, panel del inspector, «Guardar como plantilla», exportar e importar, «Actualizar a» | ChatGPT: catálogo, miniaturas y panel; Claude: diálogo de exponer campos y revisión del flujo; Devin: textos de la guía y entradas de atajos | `plantillas/5-interfaz` (solo `src/Rodaje.App`, `tests/Rodaje.App.Tests`, `docs/guia`). Tras 2 y 3 | Pruebas de lógica de la App; paso «plantilla» de la autoprueba con las cifras de 2.4 en el informe; AOT sin advertencias; casos en `docs/pruebas-a-mano.md` para Daniel (no bloquean) |
| **6. Medida y cierre** | Cifras del RTX 3050 en este doc, CHANGELOG, `CLAUDE.md` | Claude (autoprueba a prioridad baja); revisión de bloque por otro modelo (codex), como pide la regla 4 | `plantillas/6-cierre` | Tabla 2.4 rellenada con lo medido; revisión de bloque hecha y lo que encuentre, unido o como tarea |

Archivos compartidos entre fases: `formato-proyecto.md` (0 y 2: la 2 solo añade si la 0 se quedó corta) y `EffectContent` (3 y 5: la 5 empieza después de unir la 3). Hasta la fase 5, un elemento `motion-template` instalado se ve en la página de complementos, como hoy `template`.

## 4. Riesgos

- **Capas que divergen al copiar.** Si `ClipGenerator.Clone` o `SameContent` olvidan `Template`, una copia comparte las capas con el original y el deshacer no ve el cambio. Mitigación: pruebas de mutar la copia y de deshacer en la fase 2, y la regla de que todo campo nuevo del generador entra en esos dos métodos.
- **Lo que hoy exige `Kind: text`.** Edición de texto, subtítulos, «Convertir en keyframes», atributos de clip y la búsqueda filtran por `GeneratorKinds.Text`; un `template` queda fuera de todo eso salvo que se decida otra cosa. Mitigación: en la fase 2 se listan con `git grep "GeneratorKinds.Text"` y se decide uno a uno (por defecto, quedan fuera y la fase lo documenta).
- **Proyecto más grande.** Cada rótulo lleva su documento (hasta 256 KB) y sus capas instanciadas; 200 rótulos de 20 KB son 4 MB de JSON. Mitigación: prueba de 500 rótulos que guarda y abre en menos de 1 s; si no llega, el documento pasa a una tabla del proyecto por hash (formato 17, no ahora).
- **Plantillas que el lector acepta pero se ven mal** (texto fuera de la imagen con un nombre largo). Mitigación: las de fábrica usan `box` y `fit: shrink`; la guía lo explica para las propias.
- **Fuentes que faltan.** Una plantilla de un paquete pide una fuente de `rodaje-fuentes` que no está instalada: se ve con el respaldo y aviso, como hoy en los títulos; el panel ofrece instalar el paquete que falta.
- **El tipo `template` ya existe** para plantillas de proyecto; usar otro nombre (`motion-template`) evita mezclar las dos cosas.

## 5. Preguntas para Daniel

Cada una con la opción que se toma mientras no conteste.

1. **¿Formato 16 o meter `template` en el 15 sin subir?** Con la versión mínima por archivo, subir solo afecta a proyectos con plantillas: la 1.0.x los rechaza; sin subir, la 1.0.x los abriría con los rótulos invisibles. **Se toma: 16, solo cuando hay plantillas.**
2. **¿Las plantillas se pueden editar por dentro sin convertirlas?** Tipo Resolve: mover una capa sin perder los parámetros. Es más difícil (los destinos pueden dejar de existir). **Se toma: no; «Convertir en títulos» primero.**
3. **¿Capas que no son texto (barras de color, formas, imágenes)?** Hoy una barra se hace con el fondo de un título (`background`, con `padding` y `radius`), que alcanza para rótulos. Formas y color sólido son un generador nuevo y tocan el render. **Se toma: solo texto en este bloque; el generador de formas como bloque aparte.**
4. **¿El repositorio `rodaje-plantillas` sale público con la versión que lo use?** **Se toma: se crea privado y CC0; se publica cuando Daniel lo diga** (regla de publicación).
5. **¿«Actualizar a» la versión nueva de una plantilla automático al abrir?** **Se toma: nunca automático; botón en el panel.**
6. **¿La miniatura animada se genera en la máquina o viene en el paquete?** **Se toma: se genera en la máquina** (2.3).

## 6. Fuera de este bloque

Expresiones o código en las plantillas (nunca: los paquetes no traen código), capas de imagen o video dentro de la plantilla, formas, datos desde una hoja de cálculo para generar cien rótulos (buena idea para después: es un bucle sobre `TemplateEdits.SetValues`), y marcas de agua animadas fijas a toda la secuencia.

## 7. Revisión de otro modelo (Devin, 15-nov, solo lectura)

Encargo `mesa-agentes\datos\encargos\rodaje-plantillas-revision.md`; respuesta en `mesa-agentes\datos\trabajos\devin-rodaje-plantillas-revision-20261115-161322\final.txt`. 14 puntos; comprobados en el código los de más peso (`ClipClipboard.cs:75`, `ProjectSerializer` con versión mínima, `SequenceEditCommand`, `CompositeCommand.TryMerge`).

**Incorporado:** 1, 2, 3 y 8 (historial de una sola secuencia, compuestos que no se copian, `SplitOff` que comparte la secuencia, huérfanas al borrar): resueltos cambiando el clip compuesto por un generador `template`, no parcheando cada caso. 4 (el serializador escribe la versión mínima: se añade la condición de plantilla). 5 y 6 (Packages y Core no se referencian: Core valida las capas instanciadas y guarda el documento como texto; los topes del sobre se repiten en Packages con una prueba que los ata). 7 (`FindTransition` cambia de firma y el cargador acepta `motion-template`). 9 (la ida y vuelta del ejemplo v16 pasa a la fase 2). 10 (`x`/`y` escalan con la razón del estilo). 11 (tope `minSeconds` en `TrimmingSupport`; sin relayout). 12 (valores en cada efecto copiado y aviso de que reaplicar descarta retoques). 13 (quitada la afirmación de la miniatura de la pista). 14 (catálogo en `Services/MotionTemplates`; hasta la fase 5 se ve en complementos).

**Descartado:** nada entero. Del punto 1 no se toma la vía de `CompositeCommand` con dos secuencias ni un comando multisecuencia: con el generador no hacen falta, y tocar el historial para esto sería más código y más riesgo. Del 2 no se levanta la prohibición de copiar compuestos en general: queda fuera de este bloque.
