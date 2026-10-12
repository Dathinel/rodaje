# Descarga de complementos y distribución

Cómo baja Rodaje sus paquetes, módulos y modelos, y cómo se publican. Arquitectura en D-031 y [fase-7.md](fase-7.md).

## Cómo funciona

- **Índice firmado.** La app lee `indice.json` e `indice.json.sig` de la última release de `Dathinel/rodaje-catalogo` (`releases/latest/download/…`, sin pasar por la API de GitHub). La firma es ECDSA P-256 sobre SHA-256 de los bytes exactos, en DER y en base64. Se comprueba con la clave pública que va dentro del exe (`src/Rodaje.App/Services/Packages/clave-catalogo.txt`) **antes** de leer el JSON. No se acepta un índice con `generated` más viejo que el último visto (`descargas\indice-visto.txt`).
- **Orígenes.** Cada entrada solo puede apuntar a `https://github.com/Dathinel/<repo>/releases/download/<tag>/<archivo>` o a `https://huggingface.co/<repo>/resolve/<commit de 40 hex>/<archivo>`. Las redirecciones a los CDN se siguen sin fijar el host: lo que protege es el hash firmado.
- **Archivo.** `FileDownloader` baja a `<sha256>.paquete.parcial` en `%LOCALAPPDATA%\Rodaje\complementos\descargas`, calcula el SHA-256 al vuelo, reanuda con `Range` tras un corte, una cancelación o un apagón (y si la parte previa venía dañada, la baja entera una vez), nunca acepta más bytes que el tamaño publicado, y solo con hash y tamaño correctos vacía al disco y renombra. Si no coinciden, borra el archivo.
- **Instalación.** La de siempre (`PackageInstaller`): extracción aparte y cambio de nombre, con la recuperación tras apagón al arrancar. Lo descargado se borra después de instalar.
- **Modelos.** `ModelStore` los guarda una vez por hash en `complementos\modelos\<sha256>`; la completa los trae en `modelos\` junto al exe.
- **Sin clave** en el exe, la descarga queda desactivada y la página dice que se instale desde archivo.

## Lo que hace Daniel una sola vez

1. **Crear la clave** (en tu PC, no en la nube):
   `powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\firmar-indice.ps1 -NuevaClave`
   Deja la privada en `%USERPROFILE%\.rodaje-claves\catalogo-privada.pem` (solo legible por tu usuario) y escribe la pública en `clave-catalogo.txt`. Haz commit de **ese** archivo; el `.pem` nunca se versiona (`*.pem` está en `.gitignore`).
2. **Respaldar la privada**: copia el `.pem` a un pendrive que guardes aparte y pega su contenido en tu gestor de contraseñas como nota segura. Si se pierde, los Rodaje ya publicados no aceptarán índices nuevos: la siguiente versión tendría que traer otra clave pública. Si alguien la copia, puede firmar índices: en ese caso genera otra con `-NuevaClave -Reemplazar` y publica una versión nueva de Rodaje.
3. **Crear el repo** `Dathinel/rodaje-catalogo` (público, sin código: solo releases con `indice.json` y `indice.json.sig`).

## Publicar

1. `herramientas\distribuir.ps1` publica la app (Native AOT; `Rodaje.exe` lanzador más `app\`, ver [arranque-y-distribucion.md](arranque-y-distribucion.md)) y deja en `dist\distribucion\`: `Rodaje-<versión>-base.zip`, `Rodaje-<versión>-completa.zip`, `indice.json` (sin firmar) y `SHA256SUMS.txt`. Toma el último `.paquete` de `..\Complementos\<repo>\dist\` (o `-Paquetes`), extrae cada uno en `app\paquetes\<id>` o `app\modulos\<id>` de la completa y suma los modelos de cada módulo (de `.entorno\modelos` o bajados y verificados). Un modelo de más de 2 GiB queda fuera de la completa (es el límite de un asset de GitHub) y se baja desde el gestor.
2. `herramientas\firmar-indice.ps1 -Indice dist\distribucion\indice.json` firma y comprueba que la clave pública del repo corresponde a la privada.
3. Cada `.paquete` va a la release `v<versión>` de su repo (el nombre del archivo es el de la URL del índice); `indice.json` e `indice.json.sig` a una release nueva de `rodaje-catalogo`; los zip a la release de `rodaje`.

## Pruebas

`tests/Rodaje.Paquetes.Tests/Download`, con un servidor HTTP propio en 127.0.0.1 (sockets reales): 200 archivos con un byte cambiado y 100 índices con un hash cambiado rechazados y borrados; 1000 índices o firmas alterados rechazados; cortes de red con reanudación (`Range`, solo se piden los bytes que faltan); cancelar y seguir con otro proceso; parcial dañado; servidor sin `Range`; redirecciones; un apagón en cada etapa (descarga, extracción, entre apartar la versión vieja y poner la nueva) deja siempre una versión completa; firmas hechas por `firmar-indice.ps1` aceptadas por el programa.
