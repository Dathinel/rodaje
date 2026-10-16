# Complementos

Rodaje viene completo como editor y exportador. Lo demás (transiciones, filtros con LUT, looks de cámara, estilos de texto, zonas seguras, fuentes, sonidos, preajustes de exportación, video 360, los codificadores x264/x265 y los módulos de IA) llega como **complementos**: archivos `.paquete` que se instalan desde la app. Cada uno vive en su propio repositorio de GitHub con su licencia.

## El gestor

**Complementos › Gestor de complementos...** abre la página. Esc la cierra.

- Arriba: «Abrir carpeta» (la carpeta donde se instalan), «Actualizar» (refresca la lista) e «Instalar desde archivo…».
- Sección **Contenido**: los paquetes de contenido del catálogo, todos CC0 salvo las fuentes (OFL):

  | Complemento | Qué trae |
  |---|---|
  | Transiciones (`rodaje-transiciones`) | 45 transiciones: deslizar, empujar, zoom con desenfoque, giro, barrido con luz, glitch, quemado de película, destello, luma y más |
  | Filtros (`rodaje-filtros`) | 13 looks con su LUT |
  | Cámaras (`rodaje-camaras`) | 9 LUT de log a Rec.709: S-Log3, V-Log, C-Log2, C-Log3, LogC3, BMD Film Gen5, F-Log, N-Log y HLG |
  | Títulos (`rodaje-titulos`) | 15 estilos de texto, con subtítulos y karaoke |
  | Fuentes (`rodaje-fuentes`) | 10 familias tipográficas libres |
  | Zonas (`rodaje-zonas`) | 12 guías de zonas seguras por formato |
  | Sonidos (`rodaje-sonidos`) | 50 efectos de sonido sintetizados |
  | Preajustes pro (`rodaje-preajustes-pro`) | 15 preajustes de exportación: maestros ProRes y DNxHR, emisión, cine, archivo y web |
  | Video 360 (`rodaje-360`) | activa el reencuadre de video 360 y sus preajustes |
  | Codificadores GPL (`rodaje-codificadores-gpl`, GPLv3) | x264 y x265 para exportar H.264/HEVC por CPU con la máxima calidad |

- Sección **Módulos de IA**: «Cada módulo es un programa aparte con sus modelos, que corre en tu equipo. Lo que propone se revisa antes de aplicarse y se deshace con Ctrl+Z.» Qué hace cada uno, en [IA local](ia-local.md).
- Cada fila muestra el nombre, el estado («No instalado», «Instalado · 1.0.0» o «Incluido · 1.0.0» si viene en la distribución completa), cuántos elementos trae, «Funciona en: …» y el enlace a su repositorio.

## Instalar, actualizar, desinstalar

- **Instalar**: descarga el paquete de las releases de GitHub, comprueba la firma del índice y el SHA-256 del archivo, y lo instala. Mientras baja, se ve el porcentaje y «Cancelar»; lo descargado se guarda y «Instalar» sigue desde ahí la próxima vez (también tras un apagón).
- **Actualizar**: aparece cuando el catálogo tiene una versión más nueva. Descarga y reemplaza la instalada; si algo falla a mitad, la versión anterior sigue en su sitio.
- **Desinstalar**: pregunta «¿Desinstalar «X»?». Los proyectos que usan sus elementos siguen abriendo; esos elementos se muestran como «falta el paquete» hasta que lo vuelvas a instalar. Lo incluido con el programa y lo propio no se desinstala.
- **Instalar desde archivo…**: elige uno o varios `.paquete` (por ejemplo, uno que te pasaron o uno hecho por ti). También puedes **soltar el `.paquete` en la biblioteca**.

Con más de una ventana de Rodaje abierta, desinstalar y actualizar se desactivan («Cierra las otras ventanas de Rodaje…»); instalar uno nuevo sí se puede.

Sin conexión, la página lo dice («Ahora no se pudo consultar el catálogo: …») y queda «Instalar desde archivo…». La distribución completa (`-completo.zip`) ya trae todo instalado.

## Dónde quedan

`%LOCALAPPDATA%\Rodaje\complementos\`:

| Carpeta | Qué hay |
|---|---|
| `paquetes\<id>\` | Paquetes de contenido instalados |
| `modulos\<id>\` | Módulos de IA |
| `modelos\<sha256>` | Modelos de los módulos, uno por hash (compartidos entre módulos) |
| `propios\` | Lo que creas tú: transiciones guardadas desde el inspector, estilos de texto, zonas seguras |
| `descargas\` | Descargas a medias |

## Cómo se usa lo instalado

- **Transiciones, filtros y looks de cámara**: en la biblioteca, categorías Transiciones y Filtros (riel izquierdo). Se arrastran a la línea de tiempo o al clip, o doble clic para aplicarlos al clip seleccionado.
- **LUT**: en Filtros del inspector, el filtro Color y LUT ofrece las LUT de los paquetes y «Cargar .cube…».
- **Estilos de texto**: en la categoría Texto de la biblioteca; doble clic cambia el estilo del título seleccionado.
- **Zonas seguras**: en el menú de zonas del reproductor, debajo de las de fábrica.
- **Sonidos**: en Audio › Sonidos de paquetes de la biblioteca; el botón de preescucha o el menú permiten escuchar y detenerlos, el doble clic los añade en el cursor y también puedes arrastrarlos a la línea de tiempo.
- **Preajustes de exportación y codificadores**: aparecen en el diálogo de exportar.
- **Video 360**: el efecto de reencuadre aparece en Filtros con el complemento instalado.
- **Fuentes**: quedan disponibles en la pestaña Texto. Si abres un proyecto que usa una fuente de un paquete que no tienes, el panel Texto lo avisa.

## Crear los tuyos

Un `.paquete` es un zip con `paquete.json` en la raíz y los archivos que nombra. No lleva código. Mínimo:

```json
{
  "formatVersion": 1,
  "id": "mis-transiciones",
  "version": "1.0.0",
  "kind": "content",
  "name": "Mis transiciones",
  "author": "Tu nombre",
  "license": "CC0-1.0",
  "apps": ["rodaje"],
  "items": [
    { "id": "barrido-suave", "kind": "transition", "name": "Barrido suave", "file": "transiciones/barrido-suave.json" }
  ]
}
```

Tipos de elemento: `transition` (una transición hecha con keyframes y mezcla), `filter` (un look: ajustes de color más una LUT), `effect`, `lut` (`.cube` 3D), `text-style`, `safe-zones`, `font` (ttf/otf con su licencia), `template`, `sound` y `export-preset`. La forma de cada JSON está en [formato-paquete.md](../formato-paquete.md).

La forma más rápida de empezar es copiar uno de los repositorios de ejemplo, [`Dathinel/rodaje-transiciones`](https://github.com/Dathinel/rodaje-transiciones) o [`Dathinel/rodaje-filtros`](https://github.com/Dathinel/rodaje-filtros): cada uno trae el manifiesto, las fuentes y un `empaquetar.ps1` que deja `dist\<id>-<versión>.paquete`. Pruébalo con «Instalar desde archivo…».

Otra forma de empezar: lo que guardas desde la app («Guardar como transición…» en Propiedades, «Guardar como estilo» en Texto, «Guardar» en el editor de zonas seguras) queda como JSON en `complementos\propios\`; esos archivos sirven tal cual dentro de un paquete.

Para escribir un **módulo de IA** (un programa aparte, en cualquier lenguaje), la guía es [protocolo-modulos.md](../protocolo-modulos.md). Un complemento puede llevar la licencia que elija su autor (término 4 de `LICENSE`).
