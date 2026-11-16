# Render en caché de la línea de tiempo

Los tramos pesados de la secuencia se precalculan a fragmentos intermedios y se reproducen como un solo clip (D-060). Diseño completo en [propuestas/render-en-cache.md](propuestas/render-en-cache.md).

## Medición antes de construir (F0)

`RenderCacheBaselineTests` (en `Rodaje.Export.Tests`, solo con `RODAJE_PRUEBAS_LARGAS=1`). RTX 3050 Laptop, Ryzen 7 7435HS, 15-nov-2026, a prioridad baja y con el equipo compartido con otras compilaciones: las cifras son un suelo, no un techo. Fuente `testsrc2` de 5 s a 30 fps.

### Decodificación de los códecs candidatos

| Códec | Ruta | 1080p fps (× 30) | 4K fps (× 30) | Abrir + primer cuadro 1080p / 4K (ms) | MiB por 5 s 1080p / 4K |
|---|---|---:|---:|---:|---:|
| **ProRes 422 HQ** | CPU | 497 (16,6×) | 49,6 (**1,65×**) | 44 / 183 | 47 / 161 |
| ProRes 4444 | CPU | 144 (4,8×) | 43,2 (1,44×) | 108 / 300 | 72 / 244 |
| DNxHR HQX | CPU | 518 (17,3×) | 75,4 (2,51×) | 72 / 406 | 131 / 521 |
| FFV1 | CPU | 54 (1,8×) | 12,2 (0,41×) | 168 / 519 | 49 / 175 |
| HEVC Main 10 intra (NVENC) | D3D11VA | 418 (13,9×) | 173 (5,75×) | 88 / 124 | 20 / 68 |

### Fidelidad (PSNR frente al original 4:4:4 de 10 bits; barras saturadas y contornos de 1 px)

| Imagen | ProRes 422 HQ 1080p / 4K | ProRes 4444 1080p / 4K |
|---|---:|---:|
| SDR BT.709 | 45,9 / 51,4 dB (error máximo 446 de 1023, en las líneas de croma de 1 px) | 81,0 / 84,3 dB (máx. 3) |
| HDR PQ BT.2020 | 56,9 / 60,8 dB | 81,2 / 84,4 dB |

### Composición

| Escena | Dispositivo | Componer mediana / p95 (ms) | Decodificar en CPU (ms por cuadro) |
|---|---|---:|---:|
| A: 4 capas 4K, 6 efectos por capa | RTX 3050 | 23,5 / 25,2 | 85 (las cuatro capas) |
| C: 2 capas 1080p con color | RTX 3050 | 0,7 / 11,4 | 29 |
| C: 2 capas 1080p con color | WARP | 38,6 / 59,6 | 29 |

### Decisiones

- **ProRes 422 HQ en SDR y HDR, también en 4K**: la CPU lo decodifica a 1,65× la tasa en 4K30 (umbral 1,5×), con poco margen; HEVC intra por la GPU queda como respaldo si el uso real lo pide (`RenderCacheFragmentMetadata.PlaybackOnly`).
- **422 y no 4444**: 4444 no llega a 1,5× en 4K y ocupa un 50 % más. La pérdida de 422 se concentra en el croma de detalles de 1 px (error máximo 446 de 1023 en líneas rojas y azules de un píxel); el promedio cumple ≥45 dB. Se dice en la ayuda de la franja.
- **Coste por apertura** para `RenderCostModel`: unos 45 ms en 1080p y 180 ms en 4K (abrir y primer cuadro de ProRes); un fragmento abre un decodificador cada 2 s.
- **Factor sin GPU**: componer en WARP cuesta unas 54 veces lo de la GPU (38,6 frente a 0,7 ms en la escena C); es donde el caché más ayuda.
- Escenas B, D y E (desenfoque de movimiento, compuestos anidados, títulos por letra) y la medida con audio y presentación quedan para la prueba de hardware de F4.
