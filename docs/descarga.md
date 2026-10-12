# Descarga de complementos y distribución

Cómo baja Rodaje sus paquetes, módulos y modelos, y cómo se publican. Arquitectura en D-031 y el [protocolo de módulos](protocolo-modulos.md).

## Cómo funciona

- **Índice firmado.** La app lee `indice.json` e `indice.json.sig` de la última release de `Dathinel/rodaje-catalogo` (`releases/latest/download/…`, sin pasar por la API de GitHub). La firma es ECDSA P-256 sobre SHA-256 de los bytes exactos, en DER y en base64. Se comprueba con la clave pública que va dentro del exe (`src/Rodaje.App/Services/Packages/clave-catalogo.txt`) **antes** de leer el JSON. No se acepta un índice con `generated` más viejo que el último visto (`descargas\indice-visto.txt`).
- **Orígenes.** Cada entrada solo puede apuntar a `https://github.com/Dathinel/<repo>/releases/download/<tag>/<archivo>` o a `https://huggingface.co/<repo>/resolve/<commit de 40 hex>/<archivo>`. Las redirecciones a los CDN se siguen sin fijar el host: lo que protege es el hash firmado.
- **Archivo.** `FileDownloader` baja a `<sha256>.paquete.parcial` en `%LOCALAPPDATA%\Rodaje\complementos\descargas`, calcula el SHA-256 al vuelo, reanuda con `Range` tras un corte, una cancelación o un apagón (y si la parte previa venía dañada, la baja entera una vez), nunca acepta más bytes que el tamaño publicado, y solo con hash y tamaño correctos vacía al disco y renombra. Si no coinciden, borra el archivo.
- **Instalación.** La de siempre (`PackageInstaller`): extracción aparte y cambio de nombre, con la recuperación tras apagón al arrancar. Lo descargado se borra después de instalar.
- **Modelos.** `ModelStore` los guarda una vez por hash en `complementos\modelos\<sha256>`; la completa los trae en `modelos\` junto al exe.
- **Sin clave** en el exe, la descarga queda desactivada y la página dice que se instale desde archivo.

## Estado: activo (12-oct)

- **Clave**: creada; la privada en `%USERPROFILE%\.rodaje-claves\catalogo-privada.pem` (nunca se versiona; `*.pem` está en `.gitignore`), la pública en `clave-catalogo.txt`. Debe tener al menos dos respaldos físicos fuera del equipo. Si se pierde, los Rodaje ya publicados no aceptarán índices nuevos; si alguien la copia, se genera otra con `-NuevaClave -Reemplazar` y se publica una versión nueva de Rodaje.
- **Catálogo**: `Dathinel/rodaje-catalogo` (público, CC0, sin código), release `v1` con `rodaje-transiciones` 1.0.0 y `rodaje-filtros` 1.0.0, cada uno en la release `v1.0.0` de su repo.
- **Prueba contra lo publicado**: `CatalogoRealTests` (`--filter Categoria=Red` con `RODAJE_PRUEBAS_RED=1`; la CI y `probar.ps1` la excluyen porque necesita red) baja el índice real, verifica la firma con la clave del repo e instala los dos paquetes en una carpeta temporal; una firma o un índice alterados se rechazan.

## Publicar una versión nueva

1. **Paquete de contenido**: sube `version` en su `paquete.json`, `empaquetar.ps1` (deja `dist\<id>-<versión>.paquete`), commit y push, y `gh release create v<versión> dist\<id>-<versión>.paquete` en su repo.
2. **Índice**: `build\distribuir.ps1` (deja `Rodaje.exe` lanzador más `app\`, ver [arranque-y-distribucion.md](arranque-y-distribucion.md)) (con `-SinPublicar -SinCompleta` si solo cambia contenido y `dist\Rodaje` ya está publicado) deja `dist\distribucion\indice.json` con el último `.paquete` de cada `..\Complementos\<repo>\dist\`. La app solo lee el índice de la última release, así que **cada índice lista todo lo publicado**, no solo lo nuevo, y su `generated` es posterior al anterior.
3. **Firma**: `build\firmar-indice.ps1 -Indice dist\distribucion\indice.json` firma y comprueba que la clave pública del repo corresponde a la privada.
4. **Catálogo**: `gh release create v<n+1> indice.json indice.json.sig -R Dathinel/rodaje-catalogo`. Las releases viejas se pueden dejar: la app solo toma la última y rechaza índices más viejos que el último visto.
5. **Comprobar**: `build\dotnet-local.cmd test tests\Rodaje.Packages.Tests --filter Categoria=Red` con `RODAJE_PRUEBAS_RED=1`.

La distribución completa: `distribuir.ps1` sin opciones publica la app (Native AOT) y deja además el instalador, `Rodaje-<versión>-portable.zip`, `Rodaje-<versión>-completo.zip`, `SHA256SUMS.txt` y las notas (ver [arranque-y-distribucion.md](arranque-y-distribucion.md#versión-e-instalador-v1)); la completa trae cada `.paquete` extraído en `paquetes\<id>` o `modulos\<id>` y los modelos de cada módulo (de `.entorno\modelos` o bajados y verificados). Un modelo de más de 2 GiB queda fuera (límite de un asset de GitHub) y se baja desde el gestor. Los zip y el instalador van a la release de `rodaje` con `build\publicar-release.ps1`.

## Pruebas

`tests/Rodaje.Packages.Tests/Download`, con un servidor HTTP propio en 127.0.0.1 (sockets reales): 200 archivos con un byte cambiado y 100 índices con un hash cambiado rechazados y borrados; 1000 índices o firmas alterados rechazados; cortes de red con reanudación (`Range`, solo se piden los bytes que faltan); cancelar y seguir con otro proceso; parcial dañado; servidor sin `Range`; redirecciones; un apagón en cada etapa (descarga, extracción, entre apartar la versión vieja y poner la nueva) deja siempre una versión completa; firmas hechas por `firmar-indice.ps1` aceptadas por el programa.
