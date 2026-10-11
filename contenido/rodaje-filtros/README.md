# rodaje-filtros

Complemento opcional para [Rodaje](https://github.com/Dathinel/rodaje): 8 looks de color, cada uno un filtro con su LUT 3D (33³): cálido, frío, verde azulado y naranja, blanco y negro, desvaído, vivo, noche y cine (con viñeta). Las LUT salen de fórmulas propias en `generar.ps1` (curva en S, saturación sobre la luminancia Rec. 709 y desplazamientos por canal según la luminancia); no hay LUT ni recursos de terceros.

## Instalar en Rodaje

Descarga `rodaje-filtros-<versión>.paquete` de las releases y, en Rodaje, usa «Instalar desde archivo…» en Complementos o suéltalo sobre la biblioteca. Los looks aparecen en Filtros; las LUT sueltas también, para usarlas con la intensidad que quieras.

## Construir el paquete

```
powershell -NoProfile -ExecutionPolicy Bypass -File empaquetar.ps1
```

Solo hace falta Windows PowerShell 5.1. Genera las LUT, comprueba que existan todos los archivos del manifiesto y deja `dist\rodaje-filtros-<versión>.paquete` con orden y fecha fijos: las mismas fuentes dan el mismo archivo byte a byte. `-Salida <carpeta>` cambia dónde queda.

| Archivo | Qué es |
|---|---|
| `paquete.json` | Manifiesto: 8 `filter` y 8 `lut` |
| `filtros/*.json` | `rodaje.filter` v1: `rodaje.color` con su LUT (`./lut-<look>`, de este mismo paquete) |
| `generar.ps1` | Las fórmulas de los looks; escribe `luts/*.cube` |
| `empaquetar.ps1` | Arma el `.paquete` |

## Licencia

CC0 1.0 (dominio público), ver `LICENSE`.
