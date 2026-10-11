# Pendientes legales

Lo que falta para publicar Rodaje bajo la PolyForm Noncommercial 1.0.0 (D-030). Las consultas y los trámites son de Daniel; el material de apoyo está en [`legal.md`](legal.md) y [`LICENCIAS.md`](../LICENCIAS.md). Cada casilla se marca con la fecha y el resultado.

## Marca y registro

- [x] **Nombre «Rodaje» en la SIC** (11-oct: Daniel comprobó que no está registrado; el registro se decide antes de la versión pública). Buscarlo en la Superintendencia de Industria y Comercio (https://www.sic.gov.co), clase 9 de Niza (software descargable; mirar también la 42). Si sigue libre, decidir si se registra.
- [ ] **Registro de derechos de autor (opcional).** Ante la Dirección Nacional de Derechos de Autor (https://www.derechodeautor.gov.co), como soporte lógico. No es obligatorio para tener los derechos, pero sirve de prueba de fecha y autoría.

## Consulta con un abogado de propiedad intelectual

**Aplazada** (Daniel, 11-oct): por ahora Rodaje es para uso propio y de personas cercanas; la consulta se hace antes de publicarlo.

- [ ] **Autoría con código escrito por IA.** Buena parte del código lo escribieron agentes de IA dirigidos por Daniel. Preguntar qué protección de autor tiene ese código en Colombia y qué significa para el `Required Notice` y para conceder permisos comerciales.
- [ ] **EULA por el Windows App SDK.** La PolyForm no cumple la cláusula 3.b de Microsoft; hace falta un texto complementario. Lista de cláusulas y conclusión en `legal.md`, «Compatibilidad de la licencia de Rodaje».
- [ ] **LGPL de FFmpeg.** Confirmar la conclusión de `legal.md` (la PolyForm no impide reemplazar las DLL ni depurar la combinación) y revisar el párrafo de `THIRD-PARTY-NOTICES.txt` marcado como pendiente de revisión legal.
- [ ] **Zonas grises de «no comercial».** Uso dentro de una empresa, creadores que monetizan videos hechos con Rodaje, cursos de pago: qué cubre la licencia y si conviene una FAQ o un permiso general para algunos casos.

## Obligaciones de terceros antes de distribuir

- [ ] **Código fuente de FFmpeg.** Archivar el tarball del commit `2a571b6` de FFmpeg y el de los scripts de BtbN, y ofrecerlos junto a `dist\` o por oferta escrita (`legal.md`, «Compilación fijada»).
- [ ] **`THIRD-PARTY-NOTICES.txt` completo.** Avisos del runtime de .NET pegados enteros, bibliotecas externas de las DLL de FFmpeg, Windows App SDK, WebView2, CsWinRT y cada paquete NuGet que viaje en `dist\` (comparar con los `packages.lock.json`).
- [ ] **Patentes de H.264, HEVC y AAC.** Revisar con el abogado la exposición por decodificar y por exportar (Via LA, Access Advance), aunque la codificación vaya por hardware o Media Foundation (`legal.md`, «Patentes de códecs»).

## Licencias propias que quedan por decidir

- [x] **Repositorios de contenido** (decidido el 11-oct: siguen en CC0 y lo nuevo también, contenido libre sin restricción comercial). `Dathinel/rodaje-transiciones` y `Dathinel/rodaje-filtros` son públicos y CC0 (no se cambiaron). Decidir si siguen CC0, que permite venderlos, o pasan a otra licencia. Qué contienen hoy:
  - `rodaje-transiciones` 1.0.0: contenido real, 8 transiciones en `rodaje.preset` v2 (cuatro deslizamientos, empujar, acercar, alejar y girar), su manifiesto y `empaquetar.ps1`. Un commit (10-oct).
  - `rodaje-filtros` 1.0.0: contenido real, 8 filtros de color con sus LUT 33³ generadas por fórmulas propias en `generar.ps1` (las LUT no se versionan, salen del script). Un commit (10-oct).
  - Los dos llevan una copia de `formato-paquete.md` de Rodaje (con eso la especificación del formato ya es pública y CC0), no tienen ninguna release publicada aunque el README remite a las releases, y enlazan a `Dathinel/rodaje`, que es privado.
- [ ] **Acuerdo de contribuidor.** Antes de aceptar contribuciones externas, un acuerdo que permita relicenciar (ver [`contribuciones.md`](contribuciones.md)).
