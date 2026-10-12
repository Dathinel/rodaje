# Licencias de modulo-traducir

| Componente | Versión | Licencia | Fuente |
|---|---|---|---|
| Código del módulo | 0.1.0 | GPL-3.0-only con los términos de Rodaje (D-032) | este repositorio |
| LLamaSharp, LLamaSharp.Backend.Cpu, LLamaSharp.Backend.Vulkan.Windows | 0.27.0 | MIT | https://github.com/SciSharp/LLamaSharp |
| llama.cpp y ggml (DLL nativas dentro de los paquetes anteriores: `llama.dll`, `ggml*.dll`, `mtmd.dll`) | la de LLamaSharp 0.27.0 | MIT | https://github.com/ggml-org/llama.cpp |
| CommunityToolkit.HighPerformance (dependencia de LLamaSharp) | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| System.Linq.Async, System.Interactive.Async (dependencias de LLamaSharp) | 7.0.0 | MIT | https://github.com/dotnet/reactive |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions, Microsoft.Bcl.*, System.Numerics.Tensors (dependencias de LLamaSharp) | las del `packages.lock.json` | MIT | https://github.com/dotnet |
| Modelo Qwen3-4B-Instruct-2507, cuantización GGUF Q4_K_M | conversión de `unsloth/Qwen3-4B-Instruct-2507-GGUF` | Apache-2.0 (pesos de Alibaba Cloud, equipo Qwen) | https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507 |
| Vulkan | `vulkan-1.dll` del controlador de la GPU | no se redistribuye | — |

**Modelo:** Apache-2.0 permite redistribuirlo y usarlo comercialmente; hay que conservar el aviso de licencia y el
`NOTICE` si lo hubiera. No entra en el zip completo (pesa unos 2,5 GB): se baja aparte.

**Huella sin verificar (12-oct):** la sesión en la nube no llega a Hugging Face (el proxy lo bloquea), así que la
SHA-256 de `paquete.json` y de `build\preparar-modelos.ps1` sale de un espejo de terceros que dice ser copia
exacta del archivo de unsloth, y la URL no está fijada a un commit. Antes de publicar el módulo hay que bajar el
archivo, comprobar la huella, fijar la URL al commit y anotar el tamaño (`size`). Si la huella no coincide, el script
borra la descarga y avisa: no se instala nada sin comprobar.

**Alternativas descartadas:** NLLB-200 (CC-BY-NC, no comercial). Opus-MT (CC-BY-4.0, ~300 MB por par de idiomas, CPU)
queda como opción ligera para más adelante: un modelo por par y traducción más literal, sin contexto entre frases.
