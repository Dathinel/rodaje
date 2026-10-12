# Licencias de modulo-traducir

| Componente | Versión | Licencia | Fuente |
|---|---|---|---|
| Código del módulo | 0.1.0 | GPL-3.0-only con los términos de Rodaje (D-032) | este repositorio |
| LLamaSharp, LLamaSharp.Backend.Cpu, LLamaSharp.Backend.Vulkan.Windows | 0.27.0 | MIT | https://github.com/SciSharp/LLamaSharp |
| llama.cpp y ggml (DLL nativas dentro de los paquetes anteriores: `llama.dll`, `ggml*.dll`, `mtmd.dll`) | la de LLamaSharp 0.27.0 | MIT | https://github.com/ggml-org/llama.cpp |
| CommunityToolkit.HighPerformance (dependencia de LLamaSharp) | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| System.Linq.Async, System.Interactive.Async (dependencias de LLamaSharp) | 7.0.0 | MIT | https://github.com/dotnet/reactive |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions, Microsoft.Bcl.*, System.Numerics.Tensors (dependencias de LLamaSharp) | las del `packages.lock.json` | MIT | https://github.com/dotnet |
| Modelo Qwen3-4B-Instruct-2507, cuantización GGUF Q4_K_M | `unsloth/Qwen3-4B-Instruct-2507-GGUF`, commit `a06e946b`, `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` (2 497 281 120 bytes, SHA-256 `3605803b…7597`); pesos originales `Qwen/Qwen3-4B-Instruct-2507`, commit `cdbee75f` | Apache-2.0 (pesos de Alibaba Cloud, equipo Qwen; `LICENSE` del repositorio de Qwen, al que remite la ficha de unsloth) | https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507 |
| Vulkan | `vulkan-1.dll` del controlador de la GPU | no se redistribuye | — |

**Modelo:** Apache-2.0 permite redistribuirlo y usarlo comercialmente; hay que conservar el aviso de licencia y el
`NOTICE` si lo hubiera. No entra en el zip completo (pesa unos 2,5 GB): se baja aparte.

**Huella comprobada** (12 de octubre de 2026): el archivo se descargó del commit `a06e946b` y su SHA-256 coincide con la de
`paquete.json` y `build\preparar-modelos.ps1` y con el oid LFS que publica Hugging Face; la URL queda fijada a ese commit y
el tamaño anotado en `size`. Si la huella no coincide, el script borra la descarga y avisa: no se instala nada sin comprobar.
Los textos literales de las licencias van en `THIRD-PARTY-NOTICES.txt` de Rodaje, sección 10.

**Alternativas descartadas:** NLLB-200 (CC-BY-NC, no comercial). Opus-MT (CC-BY-4.0, ~300 MB por par de idiomas, CPU)
queda como opción ligera para más adelante: un modelo por par y traducción más literal, sin contexto entre frases.
