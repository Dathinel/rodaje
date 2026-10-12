# Licencias de modulo-vision

El código del módulo es propio (GPL-3.0-only con los términos de Rodaje, D-032): el preprocesado, la decodificación de las salidas, el filtro temporal de la máscara, el seguimiento y el reencuadre. Los modelos conservan su licencia; ninguno se versiona en el repositorio: los baja `build\preparar-modelos.ps1` (o el gestor de complementos) con su SHA-256 fijado. RMBG, RVM y YOLO se descartaron por licencia (D-034).

## Modelos

| Modelo | Para qué | Licencia | Fuente fijada | Archivo y SHA-256 |
|---|---|---|---|---|
| **MODNet** (Ke et al., ZHKKKe/MODNet) | Quitar el fondo, por omisión (retrato) | Apache-2.0 | Exportación ONNX de `huggingface.co/Xenova/modnet`, commit `fa2fa546`, `onnx/model.onnx` (fp32) | `modnet.onnx` `07c308cf…4df9` (25,9 MB) |
| **BiRefNet_lite** (Zheng et al., ZhengPeng7/BiRefNet; troncal Swin-T) | Quitar el fondo, «calidad alta» | MIT | Exportación ONNX de `huggingface.co/onnx-community/BiRefNet_lite-ONNX`, commit `de15b22b`, `onnx/model_fp16.onnx` | `birefnet_lite_fp16.onnx` `d39b897c…c402` (114,5 MB) |
| **YuNet 2023mar** (Wu, Peng, Yu; OpenCV) | Rostros | MIT | Espejo de opencv_zoo en `huggingface.co/opencv/face_detection_yunet`, commit `3cc26e7f`, `face_detection_yunet_2023mar.onnx` | `face_detection_yunet_2023mar.onnx` `8f2383e4…2fa4` (233 KB) |
| **RT-DETRv2-R18** (Lv et al., lyuwenyu/RT-DETR; pesos de PekingU en Transformers) | Personas | Apache-2.0 | Exportación ONNX de `huggingface.co/onnx-community/rtdetr_v2_r18vd-ONNX`, commit `936f90b6`, `onnx/model.onnx` (fp32) | `rtdetr_v2_r18vd.onnx` `583a236a…87e2` (81,1 MB) |

- **MODNet:** el repositorio original publica código y pesos con Apache-2.0; la exportación de Xenova declara la misma. Al redistribuir se incluye la licencia y el aviso.
- **BiRefNet_lite:** código y pesos MIT. **Ojo:** se entrenó con DIS5K, cuyos términos piden uso no comercial y de investigación, además de otros conjuntos (HRSOD, UHRSD, etc.). La licencia MIT de los pesos no hereda esa restricción de forma explícita, pero queda anotado en `docs/pendientes-legales.md` como punto a revisar antes de publicar. Se usa la variante **fp16**: en fp32 no cabe en 4 GB de VRAM (DirectML se queda sin memoria) y en la CPU tarda 10,7 s por cuadro; fp16 en la CPU tarda unos 15 s, así que la caída a la CPU de «alta» es solo de rescate.
- **YuNet:** `models/face_detection_yunet/LICENSE` de opencv_zoo, MIT. Pesos entrenados con WIDER FACE (uso de investigación de las imágenes; no viajan con el modelo).
- **RT-DETRv2:** Apache-2.0. Los pesos se entrenaron con COCO 2017: anotaciones CC-BY-4.0 (COCO Consortium) e imágenes de Flickr con sus propias licencias; las imágenes no viajan con el modelo. Solo se usa la clase «persona».
- **Lógica reimplementada:** la decodificación de YuNet sigue a `FaceDetectorYN` de OpenCV (Apache-2.0) y el preprocesado de cada modelo a sus procesadores de Transformers/Transformers.js (Apache-2.0); el código C# es propio, no una copia.

## Bibliotecas

| Biblioteca | Versión | Licencia | Nota |
|---|---|---|---|
| ONNX Runtime con DirectML (`Microsoft.ML.OnnxRuntime.DirectML`) | 1.24.4 | MIT | `onnxruntime.dll` |
| DirectML (`Microsoft.AI.DirectML`) | 1.15.4 | Licencia de software de Microsoft (redistribuible) | `DirectML.dll`; `DirectML.Debug.dll` no se distribuye |

## Imágenes de prueba (solo en `.entorno\corpus\vision`, nunca en el repo ni en la distribución)

- **Lewis M. Blanton, juez magistrado de EE. UU., con fondo transparente** — dominio público (obra del gobierno de EE. UU.), Wikimedia Commons, `retrato.png` `9130b128…d23e`.
- **Alfred Hitchcock, 1960, con fondo transparente** — dominio público, Wikimedia Commons, `cuerpo.png` `09af95f6…87c2`.

Se componen sobre fondos sintéticos (degradado, damero, ruido, color liso, rayas, manchas) y la alfa del recorte es la verdad para medir la máscara.
