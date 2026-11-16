# Fase 0 (b): Decodificación con el hwaccel Vulkan de FFmpeg en un VkDevice propio

Prototipo de consola en C# (`net10.0`, compatible con Native AOT) que valida la puerta técnica D-066 (`docs/propuestas/mac-y-linux.md`): decodificación por hardware con el hwaccel Vulkan de FFmpeg inicializado sobre un `VkDevice` creado por la aplicación (no por FFmpeg), muestreo de los planos (`AVVkFrame` en NV12 y P010) mediante una pasada compute en la misma GPU, verificación de calidad frente a decodificación CPU por software, ausencia de fugas de memoria o recursos GPU, y mecanismo de respaldo (`--forzar-subida` / `av_hwframe_transfer_data`) para entornos sin extensiones de decodificación por hardware (como lavapipe).

---

## 1. Cómo se ejecuta

### Requisitos previos en Windows
- .NET 10 SDK (utilizar `build\dotnet-local.cmd` del repositorio, definiendo `RODAJE_ENTORNO`).
- Bibliotecas compartidas de FFmpeg 9.0.2 LGPL (en `D:\Programas de uso libre\Rodaje\.entorno\ffmpeg\bin` o mediante `RODAJE_FFMPEG`).
- GPU con soporte Vulkan 1.3+ y driver instalado (`vulkan-1.dll`).

### Ejecución estándar (desarrollo / Release)
```powershell
# Configurar entorno
$env:RODAJE_ENTORNO = "D:\Programas de uso libre\Rodaje\.entorno"

# Compilar proyecto en Release sin advertencias
build\dotnet-local.cmd build experimentos\linux-fase0\vulkan-video -c Release

# Ejecutar decodificación Vulkan Video por 120 segundos
build\dotnet-local.cmd run --project experimentos\linux-fase0\vulkan-video -c Release -- --segundos 120

# Ejecutar forzando el camino de respaldo por subida de cuadros software
build\dotnet-local.cmd run --project experimentos\linux-fase0\vulkan-video -c Release -- --segundos 10 --forzar-subida
```

### Opciones de línea de comandos
- `--segundos <N>`: Duración del bucle de decodificación en segundos (por defecto 20).
- `--forzar-subida`: Fuerza el camino de respaldo (`"camino": "subida"`), omitiendo `VK_KHR_video_decode_queue` y subiendo cuadros software mediante `av_hwframe_transfer_data`.
- `--salida <ruta>`: Ruta del archivo JSON de reporte (por defecto `resultado.json`).

---

## 2. Cifras medidas en este entorno (RTX 3050 Laptop GPU)

Cifras extraídas directamente del archivo `resultado.json` tras la ejecución continua de 120 segundos (`--segundos 120`):

| Parámetro | Valor medido |
|---|---|
| **GPU** | NVIDIA GeForce RTX 3050 Laptop GPU |
| **Driver Vulkan** | 610.62.0.0 |
| **Camino de decodificación** | `vulkan-video` |
| **Extensiones de video presentes** | `VK_KHR_video_queue`, `VK_KHR_video_decode_queue`, `VK_KHR_video_decode_h264`, `VK_KHR_video_decode_h265` |
| **Extensiones de video ausentes** | Ninguna (0) |
| **Cuadros H.264 (NV12)** | 10.521 cuadros |
| **Cuadros HEVC Main10 (P010)** | 10.430 cuadros |
| **Cuadros totales decodificados** | 20.951 cuadros |
| **Rendimiento medio** | 174,58 fps (decodificación 1080p + compute YUV->RGBA + sincronización) |
| **Latencia p50 por cuadro** | 2,738 ms |
| **Latencia p95 por cuadro** | 5,718 ms |
| **Transferencias CPU camino normal** | 0 (cero lecturas fuera del intervalo de verificación) |
| **Memoria del proceso (inicio)** | 90,77 MB |
| **Memoria del proceso (mitad)** | 316,71 MB |
| **Memoria del proceso (final)** | 282,72 MB |
| **Recursos vivos al finalizar** | `VkImage = 0`, `AVFrame = 0` (0 fugas) |
| **Errores de capas de validación** | 0 |
| **Cuadros verificados contra CPU** | 704 cuadros |
| **Error medio (MAE / 255)** | 0,00565 (umbral requerido: $\le 0,00784$, equivalente a $\le 2,0 / 255$) |
| **PSNR medio** | 33,40 dB |
| **Estado de verificación** | SUPERADO (Código de salida: 0) |

---

## 3. Elección de enlace: TerraFX.Interop.Vulkan vs Silk.NET.Vulkan

Para este prototipo se evaluaron las dos opciones principales del ecosistema .NET: **TerraFX.Interop.Vulkan 1.4.328.1** y **Silk.NET.Vulkan 2.23.0**. Se seleccionó **TerraFX.Interop.Vulkan**.

### Justificación técnica
1. **Cero sobrecoste y compatibilidad ABI C pura**:
   - `TerraFX.Interop.Vulkan` genera enlaces de punteros directos (`P/Invoke` y punteros a función `delegate* unmanaged`) que reproducen exactamente las declaraciones de las cabeceras estándar de Vulkan en C (`vulkan.h`).
   - Los tipos (`VkDevice`, `VkInstance`, `VkImage`, `VkSemaphore`, etc.) son punteros nativos directos o structs de tamaño exacto. Esto permite transferir punteros directamente entre Vulkan y las estructuras C de FFmpeg (`AVVulkanDeviceContext`, `AVVulkanFramesContext`, `AVVkFrame`) sin conversiones, copias intermedias ni envoltorios de clases.
2. **Compatibilidad total con Native AOT (.NET 10)**:
   - `TerraFX` no utiliza reflexión ni generación de código IL en tiempo de ejecución (Emit), permitiendo compilación nativa completa mediante ILCompiler (`PublishAot=true`) sin ninguna advertencia de recorte (`TrimWarning`) ni advertencia AOT.
   - En contraste, `Silk.NET` utiliza cargadores de extensiones basados en reflexión y tipos gestionados intermedios que requieren configuraciones de recorte y anotaciones dinámicas para evitar problemas en AOT.
3. **Resolución dinámica sin dependencias adicionales**:
   - Se enlaza directamente a `vulkan-1.dll` en Windows y a `libvulkan.so.1` / `libvulkan.so` en Linux, simplificando la portabilidad del binario nativo compilado.

---

## 4. Órdenes exactas para Ubuntu 24.04 con driver propietario de NVIDIA

Para replicar este experimento en una máquina con Ubuntu 24.04 LTS y GPU NVIDIA:

### 1. Paquetes del sistema y driver NVIDIA
```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
    build-essential \
    curl \
    wget \
    xz-utils \
    libvulkan1 \
    libvulkan-dev \
    vulkan-tools \
    clang \
    zlib1g-dev

# Instalar el driver propietario de NVIDIA (versión 550 o 560 recomendada para Vulkan Video)
sudo ubuntu-drivers install
# O específicamente: sudo apt-get install -y nvidia-driver-550
# Reiniciar el sistema si se acaba de instalar el driver
sudo reboot
```

### 2. Descarga de FFmpeg LGPL compartido (BtbN)
```bash
mkdir -p $HOME/ffmpeg-btbn
cd $HOME/ffmpeg-btbn
wget https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-lgpl-shared.tar.xz
tar -xf ffmpeg-master-latest-linux64-lgpl-shared.tar.xz --strip-components=1

# Exportar variables de entorno
export RODAJE_FFMPEG="$HOME/ffmpeg-btbn"
export LD_LIBRARY_PATH="${RODAJE_FFMPEG}/lib:${LD_LIBRARY_PATH:-}"
export PATH="${RODAJE_FFMPEG}/bin:${PATH:-}"
```

### 3. Comprobar soporte Vulkan Video en el driver NVIDIA
```bash
# Verificar que las extensiones de video aparecen en el driver
vulkaninfo | grep -E "VK_KHR_video|VK_KHR_video_decode"
```

### 4. Compilación con Native AOT
```bash
cd experimentos/linux-fase0/vulkan-video

# Publicar binario nativo AOT
dotnet publish VulkanVideo.csproj -r linux-x64 -c Release /p:PublishAot=true -o publish
```

### 5. Orden de prueba de 10 minutos (600 segundos)
```bash
# Ejecutar la prueba continua de 10 minutos (600 s)
./publish/vulkan-video --segundos 600 --salida resultado.json

# Verificar resultados en el JSON generado
cat resultado.json
```
