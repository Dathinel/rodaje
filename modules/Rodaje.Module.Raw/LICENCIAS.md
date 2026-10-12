# Licencias de modulo-raw

El código del módulo es propio (GPL-3.0-only con los términos de Rodaje, D-032). Lo que trae de terceros conserva su licencia; nada de eso se versiona en el repositorio: lo baja `build\preparar-entorno.ps1` a `.entorno\libraw` con su SHA-256 fijado y la compilación lo copia junto al exe.

## Bibliotecas

| Biblioteca | Versión | Licencia | Fuente fijada | Nota |
|---|---|---|---|---|
| **LibRaw** (LibRaw LLC) | 0.22.2, `libraw.dll` oficial de 64 bits | LGPL-2.1 o CDDL-1.0, a elección: se usa la **LGPL-2.1** | `https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip`, SHA-256 `ac64fa12bb00a7581332d4c6ab918c0533fb3f119d6b668d47a6875410dca948` (el zip trae también el código fuente) | DLL dinámica sin modificar, cargada por su API en C: se puede cambiar por otra compilación compatible. Al distribuir el módulo van `LICENSE.LGPL`, `COPYRIGHT` (en `licencias\libraw\`) y el código fuente correspondiente: el mismo zip o `LibRaw-0.22.2.tar.gz` (SHA-256 `de86b035655accff8d4010f1a221fdf50d353cb7b1422ba26f14a0db92612cfa`). |
| Código de terceros dentro de LibRaw | — | dcraw (Dave Coffin, sin las partes restringidas), DCB y FBDD (BSD de 3 cláusulas), X3F (BSD), partes del Adobe DNG SDK 1.4 (MIT) | `COPYRIGHT` de LibRaw | Se conserva `COPYRIGHT` con el aviso de cada parte. |
| Runtime de Visual C++ 2015-2022 (`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`) | el del sistema | Términos de Visual Studio («Distributable Code», redistribuible junto a la aplicación) | Microsoft | `libraw.dll` lo importa. Casi todos los Windows lo tienen; si falta, el módulo dice «Falta libraw.dll (o el runtime de Visual C++ que necesita)». Llevarlo junto al módulo es una opción pendiente (`docs/legal.md`). |

**LGPL-2.1 con GPLv3.** La LGPL-2.1 permite enlazar la biblioteca desde un programa de cualquier licencia y, además (sección 3), pasarla a la GPL v2 o posterior, así que es compatible con la GPLv3 de Rodaje. El módulo corre en su propio proceso.

## Formatos que no entran en la v1

| Formato | Por qué |
|---|---|
| **Blackmagic RAW** (`.braw`) | El SDK es gratuito pero se baja con registro y trae su propia licencia (binarios cerrados, sin ingeniería inversa, condiciones de redistribución de `BlackmagicRawAPI.dll`). Aceptarla es una decisión de licencia del autor; su API es COM de C++. |
| **REDCODE RAW** (`.r3d`) | El R3D SDK de RED se baja con cuenta de desarrollador y acuerdo de licencia: DLL cerradas (`REDDecoder`, `REDR3D`…) redistribuibles solo con sus condiciones y avisos, y reglas sobre la marca RED. |
| **ARRIRAW** (`.ari`, `.mxf`) | El ARRI Image SDK solo se da por el programa de socios de ARRI (solicitud y acuerdo de confidencialidad); no hay descarga pública. |
| **Apple ProRes RAW** | Apple no publica un SDK de ProRes RAW para Windows. FFmpeg 8 o posterior trae un decodificador propio (`prores_raw`, también en la compilación LGPL de Rodaje), hecho sin el SDK de Apple; usarlo es una cuestión de patentes para el abogado antes de ofrecerlo. |

Las cuatro quedan como complementos posibles más adelante, cada una en su propio módulo y con su acuerdo leído (`docs/legal.md`).

## Pruebas

Los cuadros de prueba son CinemaDNG **sintéticos** (`tests/Rodaje.TestMedia/SyntheticDng.cs`, código propio): una carta de color de valores conocidos en un mosaico Bayer RGGB de 12 bits. No se usa ni se versiona material de cámara.
