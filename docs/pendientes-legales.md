# Pendientes legales

Lo que queda por resolver para distribuir Rodaje bajo la GNU GPLv3 con términos adicionales de atribución y permiso para complementos (D-032). El análisis de fondo está en [legal.md](legal.md) y las licencias de cada dependencia en [`LICENCIAS.md`](../LICENCIAS.md). Cada casilla se marca con la fecha y el resultado.

## Antes de distribuir la v1

- [x] **`THIRD-PARTY-NOTICES.txt` completo** (12-oct). Secciones 1 a 10: .NET, FFmpeg (con AOM, `PATENTS` de Google, LAME, IJG y la nota de AMD), FFmpeg.AutoGen, TerraFX, Windows App SDK y WebView2, proyección del Windows SDK y CsWinRT (texto MIT del commit `8649ee3e`), las bibliotecas de las DLL de FFmpeg (todas las etapas activas de BtbN, más mingw-w64 y winpthreads, el runtime de GCC 16.2, libiconv y gnulib, opencore-amr, soxr y los 248 crates de Rust de rav1e y librsvg: `docs/crates-ffmpeg.md`), los avisos del runtime de .NET, Inno Setup y los módulos de IA (ONNX Runtime con sus `ThirdPartyNotices`, DirectML, Whisper.net y whisper.cpp, LLamaSharp y llama.cpp con sus dependencias, LibRaw y cada modelo). Quedan dos huecos que no son de copiar texto: el de la licencia del Windows SDK (es un RTF en https://aka.ms/WinSDKLicenseURL; solo se enlaza) y el de los módulos de gnulib que entran en libiconv (licencia por archivo).
- [ ] **Código fuente de Rodaje por versión.** Cada ejecutable señala el código de su versión exacta (etiqueta `v1.0.0` o equivalente).
- [ ] **Código fuente de FFmpeg.** Archivar los tarballs del commit `2a571b6` de FFmpeg, de los scripts de BtbN y de cada biblioteca externa que fijan, y ofrecerlos junto a la descarga o por oferta escrita ([legal.md](legal.md), «Compilación fijada»). Añadir el enlace a esa fuente en «Acerca de».
- [x] **Bibliotecas de FFmpeg sin revisar** (12-oct): todas las etapas activas tienen licencia SPDX y compatibilidad en `LICENCIAS.md`, con las anclas confirmadas en gnu.org. Siguen «a confirmar» por no tener entrada en la lista de la FSF: HarfBuzz («Old MIT»), libvmaf (BSD-2-Clause-Patent), libpng (libpng-2.0) y los `PATENTS` de libjxl; y sin hacer: la nota del Anexo B en libzmq y SRT, las partes GPL de gme y los módulos de gnulib.
- [ ] **GPL dentro de las DLL «LGPL» de BtbN.** chromaprint se compila con fftw3 (GPL-2.0-or-later) y libzvbi tiene dos archivos GPL-2.0-only (`src/packet-830.*`, `src/pdc.*`). Con Rodaje bajo GPLv3 el primero es compatible y el segundo no lo sería si esos objetos quedan enlazados; en los dos casos las DLL dejan de ser «solo LGPL», que es lo que dicen la sección 2 de `THIRD-PARTY-NOTICES.txt` y el punto a) de [legal.md](legal.md). Opciones: comprobar en los símbolos de `avformat-63.dll` y `avcodec-63.dll` qué entra, recompilar sin `--enable-chromaprint` y `--enable-libzvbi`, o llevarlo al abogado. Lo mismo, con menos peso, para el `ThirdPartyNotices` de ONNX Runtime (Intel MKL: falta saber si va en el DLL del paquete).
- [x] **Logotipo e icono propios.** Diseño original del titular (Dathinel) en `assets/marca/` (SVG maestro y monocromo; iconos con `build/generar-iconos.ps1`); en el README desde el 12-oct.
- [x] **Reporte privado de vulnerabilidades** activado en GitHub (`SECURITY.md`).
- [x] **Escaneo de secretos** del historial completo con gitleaks 8.24.2 (12-oct: 968 commits, sin hallazgos).

## Para revisar con un abogado de propiedad intelectual

Por ahora Rodaje se distribuye sin fines comerciales; la consulta es necesaria antes de cualquier distribución comercial y recomendable antes de la primera versión pública.

- [ ] **Autoría con código escrito con IA.** Buena parte del código se escribió con asistentes de IA dirigidos por el autor. Qué protección de autor tiene ese código en Colombia y qué significa para el copyright de `LICENSE` y para hacer valer los términos de atribución.
- [ ] **Términos adicionales de la Sección 7.** Si los términos 1 a 3 caben en 7(b) y 7(c), en especial el 2 (avisos en tiendas y páginas de venta), que podría ser una «further restriction» eliminable ([legal.md](legal.md), punto c).
- [ ] **Permiso para complementos.** Redacción del término 4 frente a la Sección 7 y a las excepciones de la FSF, y si cubre módulos propios que lleven DirectML u otros binarios de Microsoft.
- [ ] **Windows App SDK.** Si la excepción de «System Libraries» lo cubre o hace falta un permiso adicional de enlace, y qué texto de usuario final cumple su cláusula 3.b (punto b). Revisar el aviso sobre los binarios de Microsoft que muestra el instalador.
- [ ] **LGPL de FFmpeg.** Confirmar la compatibilidad con la GPLv3 y el párrafo de `THIRD-PARTY-NOTICES.txt` marcado como pendiente de revisión legal (punto a).
- [ ] **Activos reservados.** Cómo se combina la reserva del logotipo y el icono de `TRADEMARK.md` con la GPLv3 si viajan dentro del ejecutable.
- [ ] **Patentes de H.264, HEVC y AAC.** Exposición al decodificar y al exportar (Via LA, Access Advance), aunque la codificación vaya por GPU o Media Foundation, a la vista de la nota de AMD ([legal.md](legal.md), «Quién cubre las patentes»). El resultado se anota como decisión en [decisiones.md](decisiones.md).
- [ ] **Licencias de patentes de las bibliotecas.** Terminación defensiva de la AOM Patent License 1.0 (SVT-AV1, libaom), declaraciones de Opus ante la IETF y, si se llegara a usar, el binario de OpenH264 de Cisco (punto e).
- [x] **Nombres de redes en las zonas seguras del visor** (resuelto: la app ya no nombra ninguna red; preajustes, formatos y zonas usan nombres genéricos por tipo y forma). Los juegos de fábrica se llaman «TikTok», «Instagram Reels», «YouTube Shorts», «Facebook Reels»… solo para decir a qué interfaz se refieren, con dibujo propio y sin logotipos ([formato-paquete.md](formato-paquete.md), `safe-zones`). Confirmar que basta y si conviene una nota de marcas ajenas en la app.

## Marca y registro

- [x] **Nombre «Rodaje» en la SIC:** no está registrado (consulta del 11 de octubre de 2026).
- [ ] **Búsqueda de marcas** en USPTO y EUIPO, clases 9 y 42 de Niza, y decidir si se registra en la SIC. Con la GPLv3, el nombre es lo que impide que otro venda «Rodaje» tal cual: `TRADEMARK.md` lo reserva, pero sin registro su fuerza es menor.
- [ ] **Registro de derechos de autor (opcional)** ante la Dirección Nacional de Derechos de Autor (https://www.derechodeautor.gov.co), como soporte lógico. No es obligatorio, pero sirve de prueba de fecha y autoría.

## Complementos y módulos

- [x] **Repositorios de contenido:** CC0. `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` (1.0.0, publicados en el catálogo) y `Dathinel/rodaje-catalogo` son públicos y CC0; los dos primeros incluyen una copia de `formato-paquete.md`, así que la especificación del formato también es CC0.
- [ ] **`rodaje-codificadores-gpl`, antes de publicarlo:** compilar el `ffmpeg.exe` GPL propio decidido en D-034 (sin hacer: hasta compilarlo, el complemento sigue con el binario `win64-gpl-9.0` de BtbN; Docker Desktop está instalado en el equipo de desarrollo y los scripts de BtbN lo admiten) y publicar su fuente correspondiente completa; revisar el riesgo de patentes de H.264 y HEVC codificados en software para quien lo distribuya ([legal.md](legal.md), «Complementos con otras licencias»).
- [ ] **BiRefNet_lite** («calidad alta» del módulo de visión): pesos MIT entrenados con DIS5K, de uso no comercial. Confirmar si los pesos se pueden redistribuir sin heredar esa condición; si no, dejarlo como descarga aparte o quitarlo de una distribución comercial. Lo mismo, con menos riesgo, para YuNet (WIDER FACE) y RT-DETRv2 (COCO).
- [x] **Kokoro-82M** (12-oct): ficha del modelo (commit `f3ff3571`) anotada en `modules/Rodaje.Module.Voice/LICENCIAS.md` y copiada literal en `THIRD-PARTY-NOTICES.txt`, sección 10: audio permisivo o no protegido más sintético de TTS cerrados, unas pocas cientos de horas no públicas, y dos fuentes CC BY (Koniwa, SIWIS).
- [x] **Qwen3-4B** (12-oct): URL fijada al commit `a06e946b` de `unsloth/Qwen3-4B-Instruct-2507-GGUF`, 2 497 281 120 bytes, SHA-256 `3605803b…7597` comprobada sobre el archivo descargado (coincide con el oid LFS), en `build/preparar-modelos.ps1` y en el `paquete.json` del módulo.
- [ ] **`modulo-raw`:** si el runtime de Visual C++ viaja con el módulo (redistribuible según Visual Studio; probablemente «System Library»), y si más adelante se aceptan los SDK de Blackmagic RAW, RED o ARRI, cada uno con su acuerdo. ProRes RAW, pendiente por patentes de Apple.
- [x] **Licencia de los repositorios de módulos** (12 de octubre de 2026): la misma que Rodaje, GPL-3.0-only con sus términos (D-032), confirmada por el autor; coinciden los `LICENCIAS.md` y `paquete.json` de `modules/*`. Privados hasta publicarlos (D-034).
- [x] **Acuerdo de contribuidor** (resuelto con el DCO 1.1, inbound = outbound; ver contribuciones.md) antes de aceptar contribuciones externas ([contribuciones.md](contribuciones.md)).
