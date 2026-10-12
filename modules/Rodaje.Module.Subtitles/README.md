# modulo-subtitulos

Módulo de IA de Rodaje: transcribe voz y devuelve segmentos y palabras con tiempos. Proceso aparte que habla el protocolo v1 (`src/Rodaje.Modules.Protocol`); el editor le pasa el audio decodificado por memoria compartida.

- **Motor:** whisper.cpp vía Whisper.net 1.9.1 (MIT), con Vulkan (solo el `vulkan-1.dll` del driver) y CPU de respaldo.
- **Modelo:** whisper large-v3-turbo q5_0 (MIT) en la GPU; sin Vulkan o si la GPU falla, whisper small q5_1 (MIT) en la CPU (D-034: 3,1 min de FLEURS a 3,9× tiempo real con WER 10,2 %, frente a 0,8× y 3,6 % del grande en la CPU; base q5_1 iba a 8,6× con WER 28,5 %). `build\preparar-modelos.ps1` los baja a `.entorno\modelos` con SHA-256 fijado; si falta el menor, la CPU usa el grande. Desde 1.1 el paquete trae también base q5_1 (57 MB) para equipos lentos.
- **Palabras:** Whisper.net expone el DTW de whisper.cpp por token (`WhisperFactoryOptions.UseDtwTimeStamps`, `HeadsPreset = LargeV3Turbo`, `WhisperToken.DtwTimestamp`, en centésimas). Los tokens se agrupan en palabras por el espacio inicial; el inicio de cada palabra es el DTW de su primer token. Comprobado: no hace falta P/Invoke directo a `whisper.dll` ni la alternativa Parakeet.

## Función `subtitulos`

Entrada: un búfer `audio` f32 mono a 16 kHz; opcionalmente un segundo búfer `bytes` donde escribir el resultado si pasa del límite de 1 MiB de un mensaje.

Parámetros (todos opcionales): `language` (`auto` o código ISO, p. ej. `es`), `device` (`auto`, `gpu`, `cpu`), `model` (ruta del `.bin` de la GPU), `cpuModel` (el de la CPU), `prompt`, `threads` y, desde 1.1, `parallel` (1–4 tramos de ~28 s a la vez en grupos seguidos, cada uno con su estado de Whisper; el modelo se comparte). Rodaje los elige con su perfil de IA (D-037): turbo en la GPU desde ~1,6 GB libres de VRAM, small en la GPU con menos, small en la CPU y base con «Calidad de la IA › Rápida» o poca RAM.

Resultado (tiempos en segundos del audio):

```json
{ "language": "es", "device": "vulkan", "segments": [ { "start": 0.0, "end": 3.2, "text": "Hola a todos.",
  "words": [ { "start": 0.12, "end": 0.48, "text": "Hola", "p": 0.98 } ] } ] }
```

Con el búfer `bytes`, `value` es `{ "bytes": n }` y el JSON va en los primeros `n` bytes del búfer.

**Modelo:** se busca en `model`, en `modelos\` junto al ejecutable y en `RODAJE_MODELOS`. **VRAM:** `vramMb` 1500 en `paquete.json`.
