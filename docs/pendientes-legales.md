# Pendientes legales

Lo que queda por resolver para distribuir Rodaje bajo la GNU GPLv3 con términos adicionales de atribución y permiso para complementos (D-032). El análisis de fondo está en [legal.md](legal.md) y las licencias de cada dependencia en [`LICENCIAS.md`](../LICENCIAS.md). Cada casilla se marca con la fecha y el resultado.

## Antes de distribuir la v1

- [ ] **`THIRD-PARTY-NOTICES.txt` completo.** Hoy solo trae el runtime de .NET y una nota sobre FFmpeg. Faltan los avisos de las bibliotecas dentro de las DLL de FFmpeg (con AOM, `PATENTS` de Google, LAME, IJG y la nota de AMD), Windows App SDK, WebView2, CsWinRT, FFmpeg.AutoGen, TerraFX y cada paquete NuGet que viaja en la distribución (comparar con los `packages.lock.json`).
- [ ] **Código fuente de Rodaje por versión.** Cada ejecutable señala el código de su versión exacta (etiqueta `v1.0.0` o equivalente).
- [ ] **Código fuente de FFmpeg.** Archivar los tarballs del commit `2a571b6` de FFmpeg, de los scripts de BtbN y de cada biblioteca externa que fijan, y ofrecerlos junto a la descarga o por oferta escrita ([legal.md](legal.md), «Compilación fijada»). Añadir el enlace a esa fuente en «Acerca de».
- [ ] **Bibliotecas de FFmpeg sin revisar.** Leer en el commit que fija BtbN la licencia de librist, libbluray y las que no tienen fila en `LICENCIAS.md` (Fontconfig, GLib, cairo y Pango de librsvg, Vulkan y shaderc de libplacebo, Little CMS, libvorbis, rav1e y el resto), buscar la nota «Incompatible With Secondary Licenses» en libzmq y SRT, y confirmar en gnu.org las marcadas «a confirmar» (HarfBuzz, libvmaf, `PATENTS` de libjxl).
- [x] **Logotipo e icono propios.** Diseño original del titular (Dathinel) en `assets/marca/` (SVG maestro y monocromo; iconos con `build/generar-iconos.ps1`).
- [ ] **Reporte privado de vulnerabilidades** activado en GitHub (`SECURITY.md`).
- [ ] **Escaneo de secretos** del historial completo con gitleaks.

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
- [ ] **Nombres de redes en las zonas seguras del visor.** Los juegos de fábrica se llaman «TikTok», «Instagram Reels», «YouTube Shorts», «Facebook Reels»… solo para decir a qué interfaz se refieren, con dibujo propio y sin logotipos ([formato-paquete.md](formato-paquete.md), `safe-zones`). Confirmar que basta y si conviene una nota de marcas ajenas en la app.

## Marca y registro

- [x] **Nombre «Rodaje» en la SIC:** no está registrado (consulta del 11 de octubre de 2026).
- [ ] **Búsqueda de marcas** en USPTO y EUIPO, clases 9 y 42 de Niza, y decidir si se registra en la SIC. Con la GPLv3, el nombre es lo que impide que otro venda «Rodaje» tal cual: `TRADEMARK.md` lo reserva, pero sin registro su fuerza es menor.
- [ ] **Registro de derechos de autor (opcional)** ante la Dirección Nacional de Derechos de Autor (https://www.derechodeautor.gov.co), como soporte lógico. No es obligatorio, pero sirve de prueba de fecha y autoría.

## Complementos y módulos

- [x] **Repositorios de contenido:** CC0. `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` (1.0.0, publicados en el catálogo) y `Dathinel/rodaje-catalogo` son públicos y CC0; los dos primeros incluyen una copia de `formato-paquete.md`, así que la especificación del formato también es CC0.
- [ ] **`rodaje-codificadores-gpl`, antes de publicarlo:** compilar el `ffmpeg.exe` GPL propio decidido en D-034 (hoy el script empaqueta el de BtbN) y publicar su fuente correspondiente completa; revisar el riesgo de patentes de H.264 y HEVC codificados en software para quien lo distribuya ([legal.md](legal.md), «Complementos con otras licencias»).
- [ ] **BiRefNet_lite** («calidad alta» del módulo de visión): pesos MIT entrenados con DIS5K, de uso no comercial. Confirmar si los pesos se pueden redistribuir sin heredar esa condición; si no, dejarlo como descarga aparte o quitarlo de una distribución comercial. Lo mismo, con menos riesgo, para YuNet (WIDER FACE) y RT-DETRv2 (COCO).
- [ ] **Kokoro-82M:** sus datos de entrenamiento no son todos públicos; anotar la ficha del modelo antes de publicar el módulo de voz.
- [ ] **Qwen3-4B:** comprobar la huella del archivo, fijar la URL a un commit de Hugging Face y anotar el tamaño antes de publicar el módulo de traducción.
- [ ] **`modulo-raw`:** si el runtime de Visual C++ viaja con el módulo (redistribuible según Visual Studio; probablemente «System Library»), y si más adelante se aceptan los SDK de Blackmagic RAW, RED o ARRI, cada uno con su acuerdo. ProRes RAW, pendiente por patentes de Apple.
- [ ] **Licencia de los repositorios de módulos** (`modulo-*`, `rodaje-codificadores-gpl`). Privados hasta publicarlos (D-034). La propuesta es la GPLv3 con los mismos términos que Rodaje (es la que declaran hoy sus `LICENCIAS.md`); el permiso para complementos permitiría cualquier otra.
- [ ] **Acuerdo de contribuidor** antes de aceptar contribuciones externas ([contribuciones.md](contribuciones.md)).
