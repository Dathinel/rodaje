# Fase 0 de Linux y macOS: pruebas de concepto

Prototipos de la puerta de D-066 (`docs/propuestas/mac-y-linux.md`). No forman parte de `Rodaje.slnx` ni del producto. El resultado medido está en `docs/mac-y-linux-fase0.md`.

| Carpeta | Parte | Qué prueba |
|---|---|---|
| `uno/` | a | App Uno `net10.0-desktop` publicada con AOT para `linux-x64`, bajo Xvfb |
| `vulkan-video/` | b | Decodificación con el hwaccel Vulkan de FFmpeg en un `VkDevice` propio |
| `mac-vt/` | c | VideoToolbox → `MTLTexture` → `VkImage` con MoltenVK |
| `shaders/` | d | Variantes de `EffectShaders` por DXC a SPIR-V frente a D3D11 WARP |
| `eco/` | e | Eco del protocolo de módulos por socket Unix y memoria compartida |

Cada carpeta trae un `ci.sh` que corre su prueba en el corredor; los lanza `.github/workflows/linux-fase0.yml`.
