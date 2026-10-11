# Formato `.paquete` (versión 0, `formatVersion` 1)

Un paquete es la forma en que llega a Rodaje todo lo que no es código del motor: transiciones, estilos de texto, efectos armados con las primitivas, filtros, LUT, plantillas y sonidos. También describe los módulos de IA, que son programas aparte. La [propuesta de módulos](propuesta-modulos.md) aprobada fija las reglas de fondo: un paquete de contenido **nunca trae código**, cada paquete vive en su propio repositorio de GitHub (Dathinel) y el formato se comparte con los demás programas de Dathinel. Por eso la lógica está en `src/Rodaje.Paquetes`, una biblioteca sin dependencias ni WinUI que puede salir tal cual a su propio repositorio cuando llegue el segundo programa.

## El archivo

Un `.paquete` es un zip normal. En la raíz va `paquete.json`; el resto son los recursos que nombran sus elementos. Las releases de GitHub suelen envolver todo en una carpeta (`rodaje-transiciones-1.0.0/paquete.json`); el instalador lo reconoce y quita esa carpeta.

```
rodaje-transiciones.paquete
├── paquete.json
├── transiciones/deslizar.json
├── transiciones/zoom-suave.json
└── vistas/deslizar.png
```

## `paquete.json`

```json
{
  "formatVersion": 1,
  "id": "rodaje-transiciones",
  "version": "1.2.0",
  "kind": "content",
  "name": "Transiciones",
  "description": "Fundidos, deslizamientos y zooms hechos con keyframes.",
  "author": "Dathinel",
  "license": "MIT",
  "repository": "https://github.com/Dathinel/rodaje-transiciones",
  "apps": ["rodaje"],
  "items": [
    { "id": "deslizar", "kind": "transition", "name": "Deslizar a la izquierda", "file": "transiciones/deslizar.json", "preview": "vistas/deslizar.png" },
    { "id": "golpe", "kind": "sound", "name": "Golpe", "file": "sonidos/golpe.wav", "license": "CC-BY-4.0" }
  ]
}
```

| Campo | Obligatorio | Qué es |
|---|---|---|
| `formatVersion` | Sí | Versión de este formato. Un programa que entiende hasta la 1 rechaza un paquete de la 2 con el mensaje «actualiza el programa». |
| `id` | Sí | Identificador estable: minúsculas ASCII, dígitos y guiones o puntos sueltos entre ellos, hasta 64 caracteres. Es también el nombre de la carpeta instalada, así que no puede ser un nombre reservado de Windows (`con`, `nul`, `aux`, `com1`…, también con punto: `nul.x`). `propios` está reservado. |
| `version` | Sí | Versión semántica `mayor.menor.parche`, con parte previa (`-beta.2`) y de compilación (`+abc`) opcionales; se compara según SemVer 2.0. |
| `kind` | No | `content` (por defecto) o `module`. |
| `name` | Sí | Nombre para mostrar, hasta 80 caracteres. |
| `description` | No | Una frase, hasta 500 caracteres; admite saltos de línea. |
| `author` | Sí | Quién lo hizo, hasta 80 caracteres. |
| `license` | Sí | Identificador SPDX (`MIT`, `CC-BY-4.0`…), hasta 64 caracteres. Es la base del fondo legal: solo se publica contenido propio o con licencia que permita redistribuirlo. |
| `repository` | No | Repositorio de origen: solo `https`, sin usuario ni contraseña y con el dominio en ASCII (se rechazan los dominios internacionales, también escritos como `xn--`, porque permiten imitar a otro con letras parecidas). |
| `apps` | Sí | Programas en los que funciona, por su id (`rodaje`, `editor-audio`…). El instalador de cada programa rechaza lo que no lo nombra. |
| `items` | No | Elementos, cada uno con `id` (único en el paquete), `kind`, `name` y, si los necesita, `file`, `description`, `license` (cuando no es la del paquete) y `preview`. |
| `module` | Solo en módulos | `entry` (ejecutable relativo), `protocol` (versión del protocolo de módulos) y `functions` (lo que ofrece). |

Los tipos de elemento son `transition`, `text-style`, `effect`, `filter`, `lut`, `template` y `sound`. La biblioteca los reparte por categoría: Texto muestra `text-style`; Efectos, `effect`; Transiciones, `transition`; Filtros, `filter` y `lut`; Audio, `sound`. Las plantillas se ven en la página de complementos hasta que exista su panel. El contenido de los archivos `lut`, `filter`, `effect` y `transition` está en [Contenido de los elementos](#contenido-de-los-elementos); el de `text-style` llega en la fase 5b, y de `template` y `sound` el formato solo garantiza que el archivo existe y está dentro del paquete.

**Textos.** De todos los textos se quitan los caracteres de control y las marcas de dirección de Unicode (bidi, como U+202E), que pueden disfrazar un nombre o una extensión; las rutas que los traen se rechazan. Un texto que pasa de su largo es un error; los que no tienen uno propio (id, versión, rutas, funciones de un módulo) admiten hasta 256 caracteres, y cada función, 64. Los elementos propios y el catálogo se limpian igual, pero se recortan en lugar de rechazarse.

**Compatibilidad hacia delante.** Un campo desconocido se ignora. Un elemento de un tipo desconocido se omite con un aviso y el resto del paquete se carga, así un paquete pensado para una versión más nueva sirve en parte en una más vieja. Un `kind` de paquete desconocido sí es un error, porque cambia dónde se instala.

## Referencias estables

Un proyecto que usa un elemento de un paquete guarda `paquete@versión/elemento`, por ejemplo `rodaje-transiciones@1.2.0/deslizar` (`PackageReference`). Si al abrirlo el paquete no está instalado, el proyecto abre igual y el efecto se muestra como «falta el paquete», con el id para instalarlo. Arrastrar un elemento desde la biblioteca lleva esa referencia en el formato de arrastre `rodaje/elemento` (y como texto); los efectos que trae el motor llevan su tipo, por ejemplo `rodaje.transform`.

## Dónde viven

| Carpeta | Qué hay | Quién escribe |
|---|---|---|
| `%LOCALAPPDATA%\Rodaje\complementos\paquetes\<id>\` | Paquetes de contenido instalados | El instalador |
| `%LOCALAPPDATA%\Rodaje\complementos\modulos\<id>\` | Módulos de IA instalados | El instalador |
| `%LOCALAPPDATA%\Rodaje\complementos\propios\` | Lo que hace la persona en Rodaje, por ejemplo «Guardar como transición» del inspector | El programa |
| `paquetes\` y `modulos\` junto a `Rodaje.exe` | Lo que trae la distribución completa | Nadie: solo lectura |

`RODAJE_DATOS` cambia la carpeta de datos, como para el resto de los archivos de la persona. Si un mismo id aparece en varias carpetas, gana la versión mayor; a igual versión, el instalado sobre el incluido.

**Elementos propios.** La carpeta `propios` puede tener su propio `paquete.json` (se lee como un paquete con id `propios`) o, lo más simple, archivos `*.json` sueltos en cualquier subcarpeta, uno por elemento, con al menos `kind` y `name`, y opcionalmente `id` y `description`. Si no trae `id`, se deriva del nombre del archivo (`Mi Zoom Rápido.json` → `mi-zoom-rapido`); un `id` con el prefijo `propios.` lo pierde. Su referencia es `propios@0.0.0/<id>`. La biblioteca vigila la carpeta, los muestra sin reiniciar y los pone aparte, en «Mis transiciones» (o «Mis efectos», «Mis filtros»…).

**Transiciones del inspector.** «Guardar como transición» escribe `propios\<nombre>.json` con este formato, que es también el del archivo de un elemento `transition` dentro de un `.paquete`, así una transición propia se publica tal cual:

```json
{ "format": "rodaje.preset", "version": 1, "id": "propios.zoom-de-entrada", "name": "Zoom de entrada", "kind": "transition",
  "effect": "rodaje.transform", "durationSeconds": 0.5, "reference": { "width": 1920, "height": 1080 },
  "tracks": { "scale": [ { "t": 0, "value": 1.2, "interp": "easeOut" }, { "t": 0.5, "value": 1, "interp": "linear" } ] } }
```

Los tiempos van relativos al primer keyframe y las posiciones en píxeles de la secuencia de `reference`, para escalarlas a otra resolución (`AnimationPreset` en `Services/Inspector`). Es la versión 1 de `rodaje.preset`; los paquetes usan la 2 (abajo), y la 1 se sigue leyendo.

## Contenido de los elementos

Desde la fase 5 los paquetes traen solo **primitivas**: valores y curvas sobre los efectos que el motor ya sabe dibujar (`rodaje.color`, `rodaje.transform`…), nunca shaders ni código. Los filtros y efectos se **copian** al proyecto al aplicarlos (con `options.source` = su referencia); las LUT y las transiciones se **referencian**, y si falta el paquete el proyecto abre igual: la capa sale sin LUT y la transición se ve como fundido cruzado, con aviso.

Los cuatro lectores están en `ContentReaders` (`src/Rodaje.Paquetes/Content`). Ninguno confía en el archivo: los JSON se leen con `JsonDocument` (apto para AOT) y cualquier problema lanza `PackageContentException` con el motivo en español, que nombra el campo (`curves[0].keys[1].t tiene que ser mayor que el de la clave anterior`). Cada regla tiene su prueba.

### Reglas comunes de los JSON

| Regla | Si no se cumple |
|---|---|
| Hasta 1 MB, JSON (admite comentarios y comas finales) con un objeto en la raíz | «No es JSON válido…», «pesa más de 1 MB» |
| `format` es el del lector y `version` un entero desde 1 | Una versión mayor que la que se entiende pide «actualiza el programa» |
| `name` obligatorio, hasta 80 caracteres sin controles ni marcas bidi | «Falta name» |
| `id` opcional, con las reglas de los identificadores | El de la referencia es siempre el del manifiesto |
| Tipos de efecto en minúsculas con un punto (`rodaje.color`, `otro.efecto`); nombres de parámetro y opción ASCII empezando por letra | Un tipo desconocido se conserva en el proyecto, pero el motor no lo aplica |
| Hasta 32 efectos, 64 curvas, 16 parámetros y 16 opciones por efecto, 1000 claves por curva, opciones de hasta 256 caracteres | Se rechaza el archivo |
| Valores finitos; `scale` de 0 a 100 y `opacity` de 0 a 1 en la transformación | Se rechaza el archivo |
| Claves `{ t, value, interp }`: `t` en segundos de 0 a `durationSeconds`, estrictamente creciente; `interp` es `hold`, `linear` (por defecto), `easeIn`, `easeOut` o `easeInOut` | Se rechaza el archivo |
| `reference: { width, height }` (16 a 16384, por defecto 1920 × 1080): las posiciones van en píxeles de esa secuencia y se escalan a la del proyecto | Se rechaza el archivo |

### `lut`: `.cube` 3D

Formato Adobe Cube LUT 1.0, solo 3D: `LUT_3D_SIZE` de 2 a 65, `TITLE` opcional, `DOMAIN_MIN`/`DOMAIN_MAX` (o `LUT_3D_INPUT_RANGE min max`), comentarios con `#` y luego exactamente N³ líneas de tres números con R variando más rápido. Hasta 16 MB. Los valores se llevan del dominio a 0–1 al leer (`LutTable`). Se rechazan: LUT 1D (con el mensaje «Es una LUT 1D: Rodaje solo usa LUT 3D»), palabras clave desconocidas o después de los datos, repetidas, números no finitos, entradas de más o de menos y un dominio con mínimo ≥ máximo. Una prueba lee 1000 archivos aleatorios o dañados sin que el lector caiga con otra cosa que `PackageContentException`, y uno de 65³ se lee en unos 40 ms (el criterio es 150 ms; el proyecto se compila optimizado también en Debug para cumplirlo en las pruebas).

### `filter`: `rodaje.filter` v1

```json
{ "format": "rodaje.filter", "version": 1, "id": "cine", "name": "Cine",
  "effects": [ { "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "./lut-cine" } },
               { "type": "rodaje.vignette", "parameters": { "amount": 0.25 } } ] }
```

Una lista no vacía de efectos con valores fijos; lo que falta toma el valor por defecto del registro. Una opción que empieza por `./` nombra otro elemento **del mismo paquete**: `PackageContentStore` la cambia por `paquete@versión/elemento` al leerla, así el autor no repite la versión.

### `effect`: `rodaje.effect` v1

```json
{ "format": "rodaje.effect", "version": 1, "name": "Entrada con zoom", "anchor": "start", "durationSeconds": 0.5,
  "effects": [],
  "curves": [ { "effect": "rodaje.transform", "parameter": "scale",
                "keys": [ { "t": 0, "value": 1.3, "interp": "easeOut" }, { "t": 0.5, "value": 1 } ] } ] }
```

`anchor` es `start` (al principio del clip), `end` (al final) o `whole` (estirada a todo el clip); `durationSeconds` de hasta 600 s. Cada curva anima un parámetro de `rodaje.transform` o de un efecto de `effects`, sin repetir el par efecto/parámetro. Al aplicarlo, los efectos se copian al clip y las curvas se convierten en keyframes. Hace falta al menos un efecto o una curva.

### `transition`: `rodaje.preset` v2

```json
{ "format": "rodaje.preset", "version": 2, "id": "empujar", "name": "Empujar", "kind": "transition", "durationSeconds": 0.5,
  "mix": "rodaje.wipe", "mixParameters": { "angle": 180, "softness": 0 },
  "reference": { "width": 1920, "height": 1080 },
  "outgoing": { "x": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": -1920 } ] },
  "incoming": { "x": [ { "t": 0, "value": 1920 }, { "t": 0.5, "value": 0 } ] } }
```

Curvas de la transformación del clip que sale (`outgoing`) y del que entra (`incoming`), por parámetro (`x`, `y`, `scale`, `rotation`, `opacity`), más la **mezcla** de las dos imágenes: una transición propia del registro (`rodaje.crossfade` por defecto, `rodaje.wipe`, `rodaje.dip`…; nunca `rodaje.preset`) con sus `mixParameters` y `mixOptions` opcionales. `durationSeconds` (hasta 60 s) es la duración para la que se escribieron las curvas: al usarla se estiran a la de la transición de la pista (`t / durationSeconds` es el avance de 0 a 1). Las dos listas pueden faltar: un preajuste sin curvas es solo la mezcla. El v1 de «Guardar como transición» se lee como curvas del clip que entra con fundido cruzado.

### `PackageContentStore`

Carga el contenido de lo instalado por referencia; vive en `Rodaje.Paquetes` y no conoce el motor.

- **LUT.** `TryGetLut(referencia, out LutTable)` nunca lee el disco ni espera: si la LUT está en memoria la da; si no, la pide al grupo de hilos y devuelve `false`, y al terminar dispara `Changed` (en un hilo cualquiera). Una LUT dañada o un paquete que falta no se reintentan y `LutProblem` dice por qué. Caché con tope de 128 MB (unas 40 LUT de 65³) que suelta la menos usada.
- **Preajustes.** `FindTransition`, `FindFilter` y `FindEffect` leen el archivo la primera vez y lo guardan; devuelven `null` si falta o está dañado, y `PresetProblem` dice por qué. Se llaman al armar el plan o al aplicar, no por cuadro.
- **Versiones.** La referencia busca por id de paquete y de elemento; si la versión instalada es otra, se usa la instalada.
- **Instalar y desinstalar.** `Reload(scan)` olvida todo (también lo que faltaba) y dispara `Changed`; una carga que termina después no se guarda.

### Uso desde Rodaje (`IEffectContent`)

`IEffectContent` está en `Rodaje.Engine` (que depende de `Rodaje.Core` y `Rodaje.Playback`), así que la implementación vive en la app y envuelve el `PackageContentStore`:

- **`TryGetLut(ResourceKey key, out LutData?)`** → `store.TryGetLut(key.Reference, out LutTable t)` y `new LutData(t.Size, t.Rgb)` (sin copiar el arreglo). Para no reservar por cuadro, guardar el `LutData` de cada `LutTable` en una `ConditionalWeakTable<LutTable, LutData>`.
- **`Changed`** → el de la tienda, tal cual (el motor ya lo espera en cualquier hilo).
- **`FindTransition(reference)`** → `store.FindTransition`, convertida a `TransitionCurves`: `MixCode = EffectRegistry.Find(preset.Mix)?.Code` (si no es una transición del registro, el de `rodaje.crossfade`), `CurveKey(t / DurationSeconds, value, interp)` con la misma interpolación por nombre, y `Outgoing`/`Incoming` por parámetro. `MixParameters` y `MixOptions` todavía no tienen sitio en `TransitionCurves`: mientras el motor no los lleve, la mezcla usa sus valores por defecto (todo barrido iría de izquierda a derecha y con borde suave, y tres de los cuatro deslizamientos se verían mal).
- **Instalar y desinstalar** desde `PackageService` → `store.Reload(PackageLibrary.Scan(...))`. **Aplicar un filtro o efecto** → `FindFilter`/`FindEffect`, copiar sus efectos al clip con `options.source` y, en un efecto, convertir las curvas en keyframes según `Placement`.

## Paquetes de contenido de Dathinel

Rodaje viene completo como editor y exportador; el contenido es opcional y vive en repositorios públicos aparte, cada uno con su `README.md`, su `LICENSE` (CC0 1.0), el manifiesto, las fuentes y un `empaquetar.ps1` propio que solo necesita Windows PowerShell 5.1 y arma el `.paquete` con orden y fecha fijos (las mismas fuentes dan el mismo archivo byte a byte). Todo es contenido propio: no se tomó ninguna LUT ni recurso de terceros.

| Repositorio | Id | Trae |
|---|---|---|
| `Dathinel/rodaje-transiciones` | `rodaje-transiciones` | 8 transiciones `rodaje.preset` v2 |
| `Dathinel/rodaje-filtros` | `rodaje-filtros` | 8 looks: cada uno un filtro con su LUT 33³, generada por fórmulas propias (`generar.ps1`) |

| Transición | Curvas | Mezcla |
|---|---|---|
| Deslizar a la izquierda, derecha, arriba y abajo | El clip que entra llega desde fuera del cuadro (`x` o `y`), lineal | Barrido sin borde, con el ángulo del movimiento (180, 0, 270 y 90) |
| Empujar | El que sale se va por la izquierda y el que entra llega por la derecha, lineal | Barrido de 180° |
| Acercar | El que sale crece de 1 a 1,5; el que entra, de 0,85 a 1 | Fundido cruzado |
| Alejar | El que sale baja a 0,7; el que entra viene de 1,4 | Fundido cruzado |
| Girar | Un cuarto de vuelta y escala 1,3 de cada lado | Fundido cruzado |

Las curvas de deslizar y empujar van lineales para que el borde del barrido coincida con el del clip en movimiento; el ángulo del barrido sigue el sentido horario con y hacia abajo (90 = hacia abajo). Los looks son cálido, frío, verde azulado y naranja, blanco y negro, desvaído, vivo, noche y cine (con viñeta); cada LUT es además un elemento `lut` por su cuenta (`rodaje-filtros@1.0.0/lut-cine`).

## Instalar y desinstalar

`PackageInstaller` hace la instalación en pasos que nunca dejan una mezcla:

1. Si se da un SHA-256 esperado (lo hará la descarga de la fase 7), se compara con el del archivo antes de abrirlo.
2. Se extrae a `paquetes\.instalando-<aleatorio>`. Cada entrada del zip pasa por `SafePaths`: se rechazan rutas con raíz o unidad, `..` y `.`, segmentos vacíos, dos puntos (unidades y flujos alternos de NTFS), caracteres de control, prohibidos o de dirección (bidi), nombres reservados de Windows (`CON`, `NUL`, `COM1`…, también con extensión) y segmentos que terminan en punto o espacio, que Windows recorta y harían que dos entradas distintas acaben en el mismo archivo. Además se comprueba que la ruta final quede dentro de la carpeta, y se rechazan entradas repetidas sin distinguir mayúsculas. Hay límites contra zips maliciosos, que se comprueban antes de escribir nada: 20 000 entradas, 8 GB descomprimidos (los módulos traen modelos grandes), una tasa de compresión de 200 a 1 en cada entrada de más de 1 MB y en el paquete entero si pasa de 16 MB (muchas entradas chicas muy comprimidas esquivan el límite de cada una), y espacio libre en el disco para todo lo descomprimido más 256 MB. Durante la copia, una entrada que trae más bytes de los que declara se corta.
3. Se lee y valida `paquete.json` con sus archivos, y se comprueba que el paquete funcione en este programa.
4. Si ya hay una versión instalada, se compara: una más nueva actualiza, la misma reinstala y una más vieja se rechaza salvo que se pida (`allowDowngrade`). La carpeta anterior se aparta como `.anterior-<aleatorio>-<id>`, la nueva entra con otro cambio de nombre (sus archivos ya están escritos en el disco) y la vieja se borra después; si algo falla a mitad, la anterior vuelve a su lugar.

Desinstalar aparta la carpeta como `.desinstalando-<aleatorio>` y la borra. Solo se desinstala lo instalado por la persona: lo incluido con el programa y lo propio no.

**Cortes de luz.** `PackageService` hace las operaciones de a una (instalar, desinstalar y releer). Antes de la primera que se hace sin otras ventanas de Rodaje abiertas, y solo entonces, se resuelven los restos: `.instalando-*` y `.desinstalando-*` se borran; una `.anterior-*` se borra si la versión nueva quedó completa (manifiesto válido con su id y todos sus archivos) y, si no, vuelve a su sitio y la nueva a medias se descarta. Así un apagón en mitad de una actualización deja la versión vieja o la nueva, nunca ninguna.

**Varias ventanas de Rodaje.** Cada instancia abre sin compartir `%LOCALAPPDATA%\Rodaje\instancias\<pid>-<aleatorio>.lock` con `DeleteOnClose`: el sistema lo borra al cerrarse el proceso, también si se mata o se cuelga. Para contar las instancias (`InstanceRegistry`) se intenta abrir cada archivo: si se deja, es de un proceso que ya no existe (un apagón) y se borra; si da violación de uso compartido, la instancia está viva. Con más de una instancia:

- desinstalar está desactivado en la página de Complementos, que lo explica con un aviso («Cierra las otras ventanas de Rodaje para desinstalar o actualizar complementos») y mira el número de instancias cada 2 s mientras está abierta;
- `PackageService` rechaza desinstalar y también instalar sobre un paquete ya instalado (actualizar, reinstalar o volver atrás), aunque la interfaz falle, porque la otra ventana podría estar usando sus archivos;
- instalar un paquete nuevo sí se puede: no pisa nada de nadie;
- la limpieza de restos no corre; queda para la primera operación que se haga con esta ventana sola.

Además, instalar, desinstalar y limpiar toman `complementos\.operacion.lock` (abierto sin compartir mientras dura la operación; si otro proceso lo tiene, se espera hasta 10 s y luego se avisa). Así la limpieza nunca ve como resto la extracción en curso de otra ventana, aunque esa ventana se abra justo después de contar, y dos instalaciones del mismo id no se cruzan. El archivo se queda en la carpeta y no molesta.

## Módulos de IA

Un módulo es un paquete con `"kind": "module"` y un bloque `module`:

```json
"module": { "entry": "bin/modulo-subtitulos.exe", "protocol": 1, "functions": ["subtitulos"] }
```

Se instala en `modulos\<id>\` y la página de complementos y el panel IA de la biblioteca muestran si está instalado. En esta versión **no se ejecuta nada**: el protocolo de módulos (proceso aparte, tubería con JSON, lo devuelto se convierte en comandos normales que se deshacen con Ctrl+Z) llega en la fase 7.

## Catálogo

`src/Rodaje.App/Services/Packages/catalogo.json` viaja dentro del ejecutable y lista los complementos conocidos: id, tipo, nombre, descripción, repositorio, programas y lo que trae. También nombra los programas (`rodaje`, y los editores de audio y de fotos como «en planes») para mostrar «Funciona en». El catálogo solo informa: lo que se instala siempre pasa por la validación del manifiesto. La descarga desde las releases de GitHub se conecta en la fase 7 implementando `IPackageDownloader` (descarga el `.paquete` y su hash y los entrega al instalador); hasta entonces el botón Instalar del catálogo dice «Descarga desde GitHub: próximamente» y se instala con «Instalar desde archivo…» o soltando el `.paquete` en la biblioteca.

## Archivos

| Archivo | Qué hace |
|---|---|
| `PackageManifest.cs` | El manifiesto ya validado, sus elementos y el bloque de módulo |
| `ManifestReader.cs` | Lee y valida `paquete.json` con `JsonDocument` (sin reflexión, apto para AOT) y explica cada error en español |
| `PackageVersion.cs`, `PackageReference.cs` | Versión semántica y referencia `id@versión/elemento`, con las reglas de los identificadores |
| `SafePaths.cs` | Qué rutas de un paquete se aceptan |
| `SafeText.cs` | Limpieza y largo de los textos para mostrar, y direcciones https seguras |
| `PackageLibrary.cs` | Carpetas de paquetes, recorrido de lo instalado y elementos propios |
| `PackageInstaller.cs` | Instalar, desinstalar y limpiar restos |
| `PackageCatalog.cs` | Catálogo y la interfaz de descarga |
| `Content/ContentTypes.cs` | `LutTable` y los preajustes ya leídos (`FilterPreset`, `EffectPreset`, `TransitionPreset`) |
| `Content/ContentReaders.cs`, `CubeReader.cs`, `PresetReader.cs` | Los lectores de `.cube`, `rodaje.filter`, `rodaje.effect` y `rodaje.preset` |
| `Content/PackageContentStore.cs` | Carga y caché del contenido por referencia |
