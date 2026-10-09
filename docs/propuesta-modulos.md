# Propuesta: núcleo, contenido e IA como piezas separadas

Estado: **propuesta para decidir con Daniel** (9 de octubre de 2026). Afecta las fases 5 a 7.

## Principio

El ejecutable contiene solo lo que es *código del motor*. Todo lo que es *contenido* (transiciones, estilos de texto, filtros, plantillas, música, stickers) y toda la *IA* viven fuera, en paquetes que se pueden instalar o no. El mismo `FotoToma.exe` sirve para la edición «solo editor» y para la «completa»: lo único que cambia es qué hay instalado en las carpetas de paquetes y de módulos.

```
FotoToma.exe            motor + primitivas (siempre)
paquetes\               contenido: datos, sin código ejecutable (opcional)
modulos\                IA local: procesos aparte con sus modelos (opcional)
```

## 1. Núcleo y motor (el ejecutable)

- **`FotoToma.Core`** ya está preparado para esto: un efecto es `{ type: "texto", parameters }` y el núcleo no sabe qué hace cada tipo. Un tipo desconocido se conserva al guardar.
- **El motor trae un conjunto pequeño y fijo de primitivas en código**, que son el «vocabulario» con el que se construye todo lo demás:
  - transformar, opacidad, recorte y modos de mezcla;
  - color básico y LUT;
  - desenfoque y máscara;
  - texto con fuente, color, borde y sombra;
  - fundidos;
  - ganancia y fade de audio.
- Con eso, la edición «solo editor» ya es un editor completo y profesional, aunque sin catálogo de efectos listos.

## 2. Paquetes de contenido (`.ftpack`)

Un paquete es un zip con un `paquete.json` (id, versión, autor, **licencia**, lista de elementos) y sus recursos. **No contiene código**, así que no hay riesgo de seguridad y es compatible con Native AOT.

| Tipo de elemento | Cómo se define |
|---|---|
| Transición | Curvas de keyframes sobre primitivas (deslizar, zoom, fundido…) o, para las especiales, un *shader* HLSL corto con parámetros declarados que el motor compila en la GPU |
| Estilo y animación de texto | Fuente, colores y keyframes de entrada y salida en JSON |
| Filtro | LUT `.cube` más ajustes de color |
| Plantilla | Un proyecto `.ftoma` con «huecos» marcados (clip, texto o música para reemplazar) |
| Música, efectos de sonido, stickers y fuentes | Archivos con su licencia declarada |

- **Referencias estables.** Un proyecto que usa contenido de un paquete guarda `paquete@versión/elemento`. Si al abrirlo el paquete no está, el proyecto abre igual: el efecto se muestra como «falta el paquete X» y se ofrece instalarlo.
- **Legal.** Cada paquete declara su licencia. Solo se incluye contenido propio o con licencias que permitan redistribuirlo, y nunca recursos extraídos de CapCut u otras apps.

## 3. IA local (módulos en procesos aparte)

Cada función de IA es un **módulo**: un proceso separado con su propio entorno y sus modelos, que se descarga solo si se quiere usar. El editor habla con él por una tubería local (named pipe con JSON).

| Módulo | Motor | Qué devuelve al editor |
|---|---|---|
| Subtítulos | Whisper (whisper.cpp vía Whisper.net, CUDA o Vulkan) | Clips de texto con tiempos |
| Quitar fondo | Segmentación ONNX (BiRefNet o MODNet) con DirectML | Un video de máscara que se aplica con la primitiva de máscara |
| Cortes al ritmo | Detección de onsets propia | Marcadores en la línea de tiempo |
| Silencios | Análisis RMS o `silencedetect` | Rangos que se proponen como borrado ripple |

**Por qué en procesos aparte y no dentro del ejecutable:**
- **Aislamiento de fallos.** Con 4 GB de VRAM en la RTX 3050, un modelo que se queda sin memoria cierra su módulo, no el editor ni el proyecto abierto.
- **El ejecutable sigue pequeño y AOT.** ONNX Runtime, CUDA y los pesos de los modelos suman cientos de MB a varios GB.
- **Libertad de lenguaje.** Un módulo puede ser C# o **Python**, aprovechando las herramientas de IA que ya existen.
- **Licencias separadas** por módulo.

**Regla clave: la IA no edita directamente.** Lo que devuelve un módulo se convierte en comandos normales del núcleo (insertar texto, borrado ripple…). Por eso todo lo que hace la IA se ve antes de aplicarse y se deshace con Ctrl+Z.

Un **gestor de modelos** carga un modelo a la vez y lo descarga al terminar, por el límite de VRAM.

## 4. «Solo editor» frente a «completo»

| | Solo editor | Completo |
|---|---|---|
| Contenido | `FotoToma.exe` + DLL de FFmpeg | Lo mismo + `paquetes\` con el catálogo propio + `modulos\` con la IA |
| Tamaño aproximado | 40–70 MB | 1–4 GB, según los modelos |
| Internet | No hace falta | Solo para actualizar paquetes y módulos |

Se plantean dos formas de distribuirlo, y no se excluyen entre sí:

1. **Dos zip portables.** El base y el completo, que es el base con las carpetas ya llenas. Funciona sin conexión y se puede llevar en una memoria USB.
2. **Gestor de complementos dentro del editor.** Parte del base y descarga cada paquete o módulo desde las releases de GitHub, verificando su hash, igual que `.entorno`.

**Recomendación:** hacer las dos. El gestor es la misma lógica que arma el zip completo.

## 5. Fuera de esta propuesta, por ahora

- **Plugins con código de terceros** (DLL que se cargan dentro del editor). Con Native AOT no se pueden cargar ensamblados .NET en tiempo de ejecución. Si algún día hacen falta, serían módulos en proceso aparte con el mismo protocolo que la IA. Se recomienda no abrir esa puerta todavía.

## Decisiones que necesita Daniel

1. **Lenguaje de los módulos de IA.** La opción recomendada es C# para Whisper y ONNX, por rendimiento y para no depender de Python, dejando Python como opción para prototipos. La alternativa es Python para todo, aprovechando lo que ya existe.
2. **¿El formato de paquete y el protocolo de módulos se comparten con los futuros editores de audio y de fotos?** Si es así, conviene sacarlos a su propio repositorio cuando llegue el segundo programa.
3. **Distribución:** las dos formas, o solo una de ellas.

## Impacto en la hoja de ruta

- **Fase 5:** añade el registro de tipos de efecto, el cargador de `.ftpack` y un primer paquete propio de transiciones y textos.
- **Fase 7:** queda definida como el protocolo de módulos más los cuatro módulos.
- **Gestor de complementos:** va con la fase 7.
