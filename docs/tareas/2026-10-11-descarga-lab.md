# Servidor HTTP local para probar descargas

Rama `claude/descarga-lab` (tarea 27 de la cola), PR #26. Apoyo para el agente local de la ronda B de la fase 7 (`GitHubReleaseDownloader`).

## Hecho

- `testing/Rodaje.TestServer/` (biblioteca de apoyo, `net10.0`, sin dependencias NuGet):
  - `TestHttpServer`: `HttpListener` del BCL en `http://127.0.0.1:<puerto libre>/` (`localhost` en Windows) (puerto buscado con un `TcpListener` en el puerto 0 y hasta 20 intentos si otro proceso lo toma). `Map(ruta, respuestas…)` registra respuestas que se usan en orden, una por petición, y la última se repite: así una prueba arma «cortar a mitad → servir bien» o «500 → 503 → archivo». Rutas sin registrar dan 404. `Requests` y `RequestCount` guardan método, ruta, `Range`, `If-Range`, cabeceras y código. `DisposeAsync` corta las respuestas colgadas y espera a los manejadores.
  - `TestResponse` (registro inmutable, se ajusta con `with`): `File`, `Status` y `Redirect`; `AcceptRanges` (por defecto atiende `Range` e `If-Range` con 206 y `Content-Range`, 416 fuera del archivo; con `false` siempre 200 entero), `ETag`, `Location`, `CutAfterBytes` (resetea la conexión tras n bytes del cuerpo de esa respuesta, contados desde el inicio del rango) con `CutSignal` opcional (ver abajo), `StallAfterBytes` (se queda colgado sin cerrar), `HeaderDelay`, `ChunkSize` + `ChunkDelay` (lentitud) y `Headers` extra.
  - `CutSignal`: con ella el corte no es inmediato; el servidor manda los n bytes, completa `Reached` y espera a `Release()` (o al cierre del servidor) para cortar.
  - `ByteRange` (interno): `bytes=a-b`, `bytes=a-` y `bytes=-n`; varios rangos o texto raro se ignoran (archivo entero, como permite RFC 9110).
  - `TestPayloads`: bytes deterministas por semilla (xorshift32), `Sha256Hex` en minúsculas y `Corrupt` (mismo tamaño, un byte invertido).
  - `TestIndexSigner`: firma con el formato que propone `docs/fase-7.md` para `indice.json.sig` (ECDSA P-256 sobre SHA-256, DER RFC 3279, base64). Clave nueva por instancia; `PublicKey` (SubjectPublicKeyInfo DER) y `PublicKeyPem` para dárselas al verificador; firmas malas con `SignOtherData` (misma clave, otros bytes) y `SignWithForeignKey` (otra clave); `Verify` de referencia.
- `testing/Rodaje.TestServer/Tests/` (proyecto xunit, `net10.0`): 55 pruebas, todas pasan en Linux (6 corridas seguidas, ~1 s cada una). Cubren rangos (206, sufijo, abierto, 416, sin soporte), `If-Range` con ETag que coincide o no, 404, secuencia de respuestas, redirección a otro host (dos servidores), redirección sin seguir, bucle de redirecciones, corte a mitad con `CutSignal` y reanudación con `Range` + `If-Range` hasta el SHA-256 correcto, corte inmediato (prefijo y reanudación), corte dentro de un rango, corte que espera a la señal, cierre con un corte sin soltar, corte antes de las cabeceras, archivo corrupto, índice con firma buena y mala, lentitud (solo cota inferior), colgado contra un plazo del cliente, cierre del servidor con una respuesta colgada, cabeceras y descargas en paralelo.
- `Rodaje.slnx`: los dos proyectos en la carpeta `/testing/` (camino ampliado por Daniel en la revisión). `git merge-tree` con `origin/main` del 12-oct no da choque (main añadió otras líneas al `.slnx`).
- Restaura con `--locked-mode` (los dos `packages.lock.json` están en el commit). Ninguna prueba omitida (antes no existían).

## Cómo lo usa una prueba del descargador

```csharp
await using var server = TestHttpServer.Start();
var paquete = TestPayloads.Deterministic(1_000_000);
server.Map("/Dathinel/rodaje-filtros/releases/download/v1/a.paquete",
    TestResponse.Redirect(storage.UrlFor("/objeto").ToString()));          // como github.com → objects.githubusercontent
storage.Map("/objeto",
    TestResponse.File(paquete, "\"v1\"") with { CutAfterBytes = 300_000 }, // primer intento cortado
    TestResponse.File(paquete, "\"v1\""));                                  // el reintento reanuda con Range
using var signer = new TestIndexSigner();                                  // clave pública → verificador
server.Map("/indice.json.sig", TestResponse.File(Encoding.ASCII.GetBytes(signer.SignOtherData(indice))));
```

Qué configura la prueba para las firmas: los bytes de `indice.json` (con las URL apuntando a `server.UrlFor(...)` y el `sha256` de `TestPayloads.Sha256Hex`), el contenido de `indice.json.sig` (bueno con `Sign`, malo con `SignOtherData`, `SignWithForeignKey` o texto que no es base64) y la clave pública de `TestIndexSigner` en lugar de la del exe. El servidor no interpreta la firma: sirve los bytes que se le den, así que si la ronda B cambia el formato solo cambia `TestIndexSigner`.

## Decisiones

- **`HttpListener` y no Kestrel.** Kestrel pide `<FrameworkReference Include="Microsoft.AspNetCore.App" />`: no es un paquete NuGet, pero obliga a tener el runtime de ASP.NET Core en cada máquina que corra las pruebas (CI, `.entorno`), y no hace falta. `HttpListener` está en el BCL y en Linux usa la implementación administrada; en Linux el prefijo es `http://127.0.0.1:<puerto>/`. En Windows (HTTP.sys) un usuario sin administrador puede registrar `localhost` pero no `127.0.0.1` sin `netsh http add urlacl`, así que allí el prefijo y `BaseAddress` usan `localhost` (`OperatingSystem.IsWindows()`); sin comprobar todavía en Windows.
- **Sin HTTPS.** El descargador debe permitir que la prueba le pase un `HttpMessageHandler` o una URL base; el servidor es HTTP plano en el bucle local.
- **Respuestas en secuencia por ruta** en lugar de callbacks: cubre reintentos y reanudación sin código en la prueba, y deja el registro de peticiones para comprobar qué `Range` mandó el cliente.
- **Comportamientos de `HttpListener` a tener en cuenta** (comprobados en las pruebas): las cabeceras salen con el primer byte del cuerpo, así que `CutAfterBytes = 0` corta antes de las cabeceras (el cliente ve `HttpRequestException`) y `StallAfterBytes = 0` deja al cliente esperando la respuesta. Un `Content-Length` mentiroso no se puede simular con `HttpListener` (lo impone); el corte cubre el mismo caso del lado del cliente.
- **Bucle de redirecciones:** `SocketsHttpHandler` no lanza al pasar `MaxAutomaticRedirections`; devuelve la última 302. El descargador debe tratar un 3xx final como error.
- **Corte fiable en Windows (revisión de Daniel, 12-oct).** En Windows fallaba siempre `A_cut_breaks_the_body_and_a_range_request_resumes_it` (3911 bytes en vez de 70 000): `HttpListenerResponse.Abort()` en HTTP.sys manda un RST y Windows descarta lo que el cliente no había leído del socket. No se aflojó la aserción: `CutSignal` hace que el servidor espere, tras mandar los bytes, a que la prueba confirme que los leyó, y solo entonces corta. Las pruebas de conteo exacto (`A_cut_breaks…`, `A_cut_inside_a_range…`) la usan y exigen el número exacto en Linux y en Windows. El corte inmediato sigue disponible (es lo que verá un descargador que no expone sus lecturas) y su prueba exige un prefijo correcto y la reanudación en todas partes, y los 70 000 bytes exactos fuera de Windows. Para el agente de la ronda B: en Windows, sin señal, no supongas cuántos bytes llegan antes del corte; comprueba que reanuda desde lo que guardó.
- La biblioteca usa `DefaultItemExcludes` para no compilar `Tests\**`, igual que si las pruebas vivieran aparte.

## Pendiente (fuera de mis caminos)

- **CI de Linux:** añadir `testing/Rodaje.TestServer/Tests/Rodaje.TestServer.Tests.csproj` al trabajo de Linux de `.github/workflows/ci.yml` (el bucle actual solo recorre `tests/Rodaje.$p.Tests`). El trabajo de Windows ya lo corre al estar en `Rodaje.slnx`. Si `herramientas\probar.ps1` recorre proyectos por carpeta `tests\`, sumarlo también.
- Si la reorganización del repo (`docs/reorganizacion.md`) mueve las pruebas a `tests/`, las de aquí pueden pasar a `tests/Rodaje.TestServer.Tests/` cambiando solo la `ProjectReference`.

## Posibles choques

Ninguno con main al 12-oct: lo demás es nuevo dentro de `testing/Rodaje.TestServer/`, y el `.slnx` se une sin choque. El agente local de la ronda B puede referenciar el proyecto desde sus pruebas.

## CI y Windows

**La CI no arrancó**: desde las 18:52 UTC del 11-oct todos los trabajos de GitHub Actions fallan en unos 3 s sin logs (parece la facturación; no es por este PR). Daniel corrió las pruebas en Windows el 12-oct y falló solo la del corte (ya corregida). Verificado en Linux (SDK 10.0.401): los dos proyectos compilan con 0 advertencias y `TreatWarningsAsErrors`, 55/55 pruebas. `dotnet build Rodaje.slnx` en Linux lee la solución y para en `Rodaje.App` (NETSDK1100, proyecto de Windows), como se esperaba.

Qué comprobar en Windows:
- `herramientas\dotnet-local.cmd test testing\Rodaje.TestServer\Tests\Rodaje.TestServer.Tests.csproj` pasa sin pedir permisos de administrador ni `netsh http add urlacl` (HTTP.sys con prefijo `localhost`).
- Que el Firewall de Windows no pregunte (solo escucha en el bucle local).
- Que las pruebas de corte (`A_cut_*`, `An_immediate_cut_*`, `A_waiting_cut_*`) pasen y den `IOException`/`HttpRequestException` también con HTTP.sys (en Windows `HttpListener` no es la implementación administrada).
