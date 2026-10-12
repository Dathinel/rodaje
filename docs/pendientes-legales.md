# Pendientes legales

Lo que falta para publicar Rodaje bajo la GNU GPLv3 con términos adicionales de atribución y permiso para complementos (D-032, que sustituye a la PolyForm Noncommercial de D-030). Las consultas y los trámites son de Daniel; el material de apoyo está en [`legal.md`](legal.md) y [`LICENCIAS.md`](../LICENCIAS.md). Cada casilla se marca con la fecha y el resultado.

## Marca y registro

- [x] **Nombre «Rodaje» en la SIC** (11-oct: Daniel comprobó que no está registrado; el registro se decide antes de la versión pública). Buscarlo en la Superintendencia de Industria y Comercio (https://www.sic.gov.co), clase 9 de Niza (software descargable; mirar también la 42). Si sigue libre, decidir si se registra. Con la GPLv3 el nombre es lo que impide que otro venda «Rodaje» tal cual: `TRADEMARK.md` lo reserva, pero sin registro su fuerza es menor.
- [ ] **Logotipo e icono propios.** `TRADEMARK.md` los reserva; hoy la app no tiene logotipo ni icono definitivos. Hacerlos antes de publicar.
- [ ] **Registro de derechos de autor (opcional).** Ante la Dirección Nacional de Derechos de Autor (https://www.derechodeautor.gov.co), como soporte lógico. No es obligatorio para tener los derechos, pero sirve de prueba de fecha y autoría.

## Consulta con un abogado de propiedad intelectual

**Aplazada** (Daniel, 11-oct): por ahora Rodaje es para uso propio y de personas cercanas; la consulta se hace antes de publicarlo. Los puntos de la GPLv3 están en `legal.md`, «Compatibilidad con la GPLv3».

- [ ] **Autoría con código escrito por IA.** Buena parte del código lo escribieron agentes de IA dirigidos por Daniel. Preguntar qué protección de autor tiene ese código en Colombia y qué significa para el copyright de `LICENSE` y para hacer valer los términos de atribución.
- [ ] **Términos adicionales de la Sección 7.** Si los términos 1 a 3 caben en 7(b) y 7(c), en especial el 2 (obligaciones en tiendas y páginas de venta), que podría ser una «further restriction» eliminable (`legal.md`, punto c).
- [ ] **Permiso para complementos.** Revisar la redacción del término 4 frente a la Sección 7 y al modelo de excepciones de la FSF, y si cubre los módulos de IA propios que lleven DirectML u otros binarios de Microsoft.
- [x] **Versión de la GPL** (11-oct, commit `2d7db6d`: la cabecera de `LICENSE` dice «version 3 only (SPDX: GPL-3.0-only), not any later version», igual que `Directory.Build.props`; `legal.md`, punto f). Antes no lo decía y la Sección 14 dejaba elegir cualquier versión.
- [ ] **Windows App SDK frente a la GPLv3.** Su cláusula 3.c prohíbe someter sus archivos a copyleft y 3.b pide términos que protejan a Microsoft. Confirmar si la excepción de «System Libraries» los cubre o si hace falta un permiso adicional de enlace, y qué texto de usuario final cumple 3.b (`legal.md`, punto b).
- [ ] **LGPL de FFmpeg.** Confirmar la conclusión de `legal.md` (LGPL v3 compatible con la GPLv3) y revisar el párrafo de `THIRD-PARTY-NOTICES.txt` marcado como pendiente de revisión legal.

## Obligaciones de terceros antes de distribuir

- [ ] **Código fuente de Rodaje.** Con cada ejecutable entregado, el código fuente correspondiente o un enlace a él (GPLv3, Sección 6). Desde el 11-oct el repositorio es público y el enlace del crédito funciona para terceros; falta que cada ejecutable señale el código de su versión exacta.
- [ ] **Código fuente de FFmpeg.** Archivar el tarball del commit `2a571b6` de FFmpeg y el de los scripts de BtbN, y ofrecerlos junto a `dist\` o por oferta escrita (`legal.md`, «Compilación fijada»).
- [ ] **`THIRD-PARTY-NOTICES.txt` completo.** Avisos del runtime de .NET pegados enteros, bibliotecas externas de las DLL de FFmpeg, Windows App SDK, WebView2, CsWinRT y cada paquete NuGet que viaje en `dist\` (comparar con los `packages.lock.json`).
- [ ] **Licencias de las bibliotecas de FFmpeg sin revisar.** `LICENCIAS.md`, «Otras bibliotecas dentro de las DLL de FFmpeg»: leer en el commit que fija BtbN la licencia de librist y libbluray (code.videolan.org no respondía desde la nube) y de las que quedan sin fila (Fontconfig, GLib, cairo y Pango de librsvg, Vulkan y shaderc de libplacebo, Little CMS, libvorbis, rav1e y el resto de la lista), buscar la nota «Incompatible With Secondary Licenses» en libzmq y SRT, y confirmar en gnu.org las marcas «a confirmar» (HarfBuzz, libvmaf, `PATENTS` de libjxl). Hasta entonces no se puede decir que ninguna sea solo GPL (`legal.md`, «`ffmpeg.exe` para los proxies»).
- [ ] **Patentes de H.264, HEVC y AAC.** Revisar con el abogado la exposición por decodificar y por exportar (Via LA, Access Advance), aunque la codificación vaya por hardware o Media Foundation (`legal.md`, «Patentes de códecs»).

## Licencias propias que quedan por decidir

- [x] **Repositorios de contenido** (decidido el 11-oct: siguen en CC0 y lo nuevo también, contenido libre sin restricción comercial; se mantiene con la GPLv3, D-032). `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` son públicos y CC0. Qué contienen hoy:
  - `rodaje-transiciones` 1.0.0: contenido real, 8 transiciones en `rodaje.preset` v2 (cuatro deslizamientos, empujar, acercar, alejar y girar), su manifiesto y `empaquetar.ps1`. Un commit (10-oct).
  - `rodaje-filtros` 1.0.0: contenido real, 8 filtros de color con sus LUT 33³ generadas por fórmulas propias en `generar.ps1` (las LUT no se versionan, salen del script). Un commit (10-oct).
  - Los dos llevan una copia de `formato-paquete.md` de Rodaje (con eso la especificación del formato ya es pública y CC0), no tienen ninguna release publicada aunque el README remite a las releases, y enlazan a `Dathinel/rodaje`, público desde el 11-oct.
- [ ] **`rodaje-codificadores-gpl` antes de publicarlo** (`legal.md`, punto h): fuente correspondiente completa (`armar-codificadores-gpl.ps1 -FuenteCompleta` o un `ffmpeg.exe` propio solo con x264 y x265) y el riesgo de patentes de H.264/HEVC codificados en software para quien lo distribuya. Abogado.
- [ ] **`modulo-raw`:** si el runtime de Visual C++ viaja con el módulo y si los SDK de BRAW, R3D o ARRIRAW se aceptan más adelante (cada uno con su acuerdo). Decide Daniel; el runtime, con el abogado.
- [ ] **Licencia de los repositorios de módulos** (`modulo-*`, `rodaje-codificadores-gpl`, `rodaje-catalogo`, fase 7). La recomendación de `fase-7.md` era la PolyForm; con D-032 la propuesta pasa a GPLv3 con los mismos términos, o la licencia que Daniel quiera gracias al permiso para complementos. Decide Daniel.
- [ ] **Acuerdo de contribuidor.** Antes de aceptar contribuciones externas, un acuerdo que permita relicenciar y conceder permisos adicionales (ver [`contribuciones.md`](contribuciones.md)).
