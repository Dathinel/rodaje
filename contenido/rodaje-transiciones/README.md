# rodaje-transiciones

Complemento opcional para [Rodaje](https://github.com/Dathinel/rodaje): 8 transiciones hechas solo con curvas de la transformación y una mezcla del propio editor, sin shaders ni código.

| Transición | Cómo es |
|---|---|
| Deslizar a la izquierda, derecha, arriba y abajo | El clip nuevo entra desde fuera del cuadro y tapa al anterior (barrido sin borde que sigue al clip) |
| Empujar | El nuevo entra por la derecha y empuja al anterior fuera |
| Acercar y alejar | Fundido mientras la cámara se acerca o se aleja |
| Girar | Un cuarto de vuelta con fundido |

## Instalar en Rodaje

Descarga `rodaje-transiciones-<versión>.paquete` de las releases y, en Rodaje, usa «Instalar desde archivo…» en Complementos o suéltalo sobre la biblioteca. Aparecen en Transiciones; se sueltan sobre un corte (0,5 s por defecto). Si un proyecto usa una y el paquete falta, se ve como fundido cruzado y Rodaje avisa.

## Construir el paquete

```
powershell -NoProfile -ExecutionPolicy Bypass -File empaquetar.ps1
```

Solo hace falta Windows PowerShell 5.1. Deja `dist\rodaje-transiciones-<versión>.paquete` con orden y fecha fijos: las mismas fuentes dan el mismo archivo byte a byte. `-Salida <carpeta>` cambia dónde queda.

| Archivo | Qué es |
|---|---|
| `paquete.json` | Manifiesto: 8 `transition` |
| `transiciones/*.json` | `rodaje.preset` v2: curvas del clip que sale y del que entra, más la mezcla |
| `empaquetar.ps1` | Arma el `.paquete` |

## Licencia

CC0 1.0 (dominio público), ver `LICENSE`.
