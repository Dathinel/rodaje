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
| `id` | Sí | Identificador estable: minúsculas ASCII, dígitos y guiones o puntos sueltos entre ellos, hasta 64 caracteres. Es también el nombre de la carpeta instalada. `propios` está reservado. |
| `version` | Sí | Versión semántica `mayor.menor.parche`, con parte previa (`-beta.2`) y de compilación (`+abc`) opcionales; se compara según SemVer 2.0. |
| `kind` | No | `content` (por defecto) o `module`. |
| `name` | Sí | Nombre para mostrar. |
| `description` | No | Una frase. |
| `author` | Sí | Quién lo hizo. |
| `license` | Sí | Identificador SPDX (`MIT`, `CC-BY-4.0`…). Es la base del fondo legal: solo se publica contenido propio o con licencia que permita redistribuirlo. |
| `repository` | No | Repositorio de origen; solo `https`. |
| `apps` | Sí | Programas en los que funciona, por su id (`rodaje`, `editor-audio`…). El instalador de cada programa rechaza lo que no lo nombra. |
| `items` | No | Elementos, cada uno con `id` (único en el paquete), `kind`, `name` y, si los necesita, `file`, `description`, `license` (cuando no es la del paquete) y `preview`. |
| `module` | Solo en módulos | `entry` (ejecutable relativo), `protocol` (versión del protocolo de módulos) y `functions` (lo que ofrece). |

Los tipos de elemento son `transition`, `text-style`, `effect`, `filter`, `lut`, `template` y `sound`. La biblioteca los reparte por categoría: Texto muestra `text-style`; Efectos, `effect`; Transiciones, `transition`; Filtros, `filter` y `lut`; Audio, `sound`. Las plantillas se ven en la página de complementos hasta que exista su panel. El contenido de cada archivo (las curvas de una transición, los colores de un estilo de texto) se define en la fase 5, junto con el registro de tipos de efecto; la versión 0 del formato solo garantiza que el archivo existe y está dentro del paquete.

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

Los tiempos van relativos al primer keyframe y las posiciones en píxeles de la secuencia de `reference`, para escalarlas a otra resolución (`AnimationPreset` en `Services/Inspector`).

## Instalar y desinstalar

`PackageInstaller` hace la instalación en pasos que nunca dejan una mezcla:

1. Si se da un SHA-256 esperado (lo hará la descarga de la fase 7), se compara con el del archivo antes de abrirlo.
2. Se extrae a `paquetes\.instalando-<aleatorio>`. Cada entrada del zip pasa por `SafePaths`: se rechazan rutas con raíz o unidad, `..` y `.`, segmentos vacíos, dos puntos (unidades y flujos alternos de NTFS), caracteres de control o prohibidos, nombres reservados de Windows (`CON`, `NUL`, `COM1`…, también con extensión) y segmentos que terminan en punto o espacio, que Windows recorta y harían que dos entradas distintas acaben en el mismo archivo. Además se comprueba que la ruta final quede dentro de la carpeta, y se rechazan entradas repetidas sin distinguir mayúsculas. Hay límites contra zips maliciosos: 20 000 entradas, 8 GB descomprimidos (los módulos traen modelos grandes), una tasa de compresión de 200 a 1 en entradas de más de 1 MB y una copia que se corta si una entrada trae más bytes de los que declara.
3. Se lee y valida `paquete.json` con sus archivos, y se comprueba que el paquete funcione en este programa.
4. Si ya hay una versión instalada, se compara: una más nueva actualiza, la misma reinstala y una más vieja se rechaza salvo que se pida (`allowDowngrade`). La carpeta anterior se aparta con un cambio de nombre, la nueva entra con otro y la vieja se borra después; si algo falla a mitad, la anterior vuelve a su lugar.

Desinstalar aparta la carpeta y la borra. Solo se desinstala lo instalado por la persona: lo incluido con el programa y lo propio no. Al arrancar se limpian los restos `.instalando-*` y `.desinstalando-*` de un corte de luz.

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
| `PackageLibrary.cs` | Carpetas de paquetes, recorrido de lo instalado y elementos propios |
| `PackageInstaller.cs` | Instalar, desinstalar y limpiar restos |
| `PackageCatalog.cs` | Catálogo y la interfaz de descarga |
