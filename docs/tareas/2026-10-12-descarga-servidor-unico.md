# Un solo servidor de pruebas para el descargador

Rama `claude/descarga-servidor-unico` (tarea 61 de la cola), PR #53.

## Hecho

- **`ByteRange` (`testing/Rodaje.TestServer/ByteRange.cs`)**: un número del rango que solo tiene dígitos pero no cabe en `long` da 416 (antes se ignoraba la cabecera y salía el archivo entero con 200). Vale para el inicio, el final (la línea 80 de la tarea) y el sufijo. Lo que no es un número se sigue ignorando. Pruebas en `Tests/ByteRangeOverflowTests.cs` (11), una de ellas de punta a punta contra el servidor.
- **Manejador (`TestHttpServer.cs`)**: observa toda excepción. Las de un cliente que corta o del cierre siguen cortando la conexión, igual que antes. Cualquier otra (una respuesta mal configurada) se guarda en la nueva propiedad `Faults` y corta con un 500 truncado. En Linux, `HttpListenerResponse.Abort()` sin cabeceras mandaba un **200 vacío** (lo comprobé: el cliente recibía una respuesta válida), por eso antes de cortar se anuncia un 500 con un cuerpo que no llega. La lectura de la ruta, que quedaba fuera del `try`, ahora está dentro. Pruebas en `Tests/HandlerFaultTests.cs` (3): la conexión se corta enseguida, el servidor sigue sirviendo después y un cliente que cuelga no cuenta como fallo.
- **Bucle de aceptación**: un fallo de `GetContextAsync` con el servidor abierto ya no lo apaga. Se anota en `Faults` y se sigue escuchando, con un tope de 100 fallos para no girar sin fin.
- **Puerto libre en IPv4 e IPv6**: el puerto se busca en 127.0.0.1 y se comprueba que también esté libre en `::1` (si no, se prueba otro, hasta 20 veces). Si no hay IPv6, solo en IPv4. Las sondas escuchan solo en el bucle local, para que el Firewall de Windows no pregunte.
- **`TestIndexSigner`** firma y verifica con `IndexSignature` (el proyecto `Rodaje.TestServer` referencia ahora a `Rodaje.Paquetes`). Conserva las ayudas de firmas malas (`SignOtherData`, `SignWithForeignKey`) y añade `PublicKeyBase64` (la forma de `GitHubReleaseOptions.PublicKey`) y `SignFile` (los bytes de `indice.json.sig`). Pruebas en `Tests/SignerMatchesIndexSignatureTests.cs` (2).
- **Migración**: `FileDownloaderTests`, `GitHubReleaseDownloaderTests`, `ModelStoreTests` y también `CatalogoRealTests` (usaba `LocalHttpServer` en una prueba de red) pasan a `TestHttpServer`. Se borra `LocalHttpServer.cs`. Se conservan todas las pruebas y sus aserciones, y además:
  - la reanudación con conteo exacto usa `CutSignal`, que el descargador suelta desde su progreso, así el número es exacto también en Windows; el apagón de `GitHubReleaseDownloaderTests` también;
  - prueba nueva de corte inmediato (sin señal): en Windows solo exige un prefijo y la reanudación, y fuera de Windows exige los 200 000 bytes exactos;
  - los paquetes se sirven como en GitHub: la dirección de la versión redirige a un segundo servidor («almacenamiento»), y la reanudación se comprueba ahí (`Range` y 206);
  - 503 y 500 reintentados a través de una redirección a otro servidor; bucle de redirecciones que falla sin dejar nada;
  - archivo que cambia entre el corte y la reanudación (otro ETag): el hash lo rechaza y no queda nada;
  - firma mala como teoría: otra clave, otros datos, texto que no es base64, vacía y demasiado larga (antes solo «otra clave»);
  - cada clase cierra sus servidores en `IAsyncLifetime.DisposeAsync` y exige `Faults` vacío.
- **CI**: el bucle de pruebas de Linux corre también `testing/Rodaje.TestServer/Tests` (nada más cambia en `ci.yml`).
- Lockfiles actualizados con `--force-evaluate`. `dotnet restore --locked-mode` pasa en los tres proyectos y en `Rodaje.slnx` con `-p:EnableWindowsTargeting=true`.

## Fallo de la CI de Windows en `Status_responses_carry_their_text` (aviso del coordinador)

En el job 114759589713 (PR #47) la prueba venció el `HttpClient.Timeout` de 30 s. El log muestra al cliente en `HttpConnection.InitialFillAsync`: la conexión TCP se aceptó y la petición se mandó, pero nunca llegó respuesta. La `IOException`/`SocketException` «I/O operation has been aborted» es la del cliente al cancelar por el plazo. La prueba en sí no tiene nada raro (un 500 con texto). Las causas compatibles con «conexión aceptada sin respuesta» son:

1. **Un manejador que lanza fuera de los tipos filtrados** (`TestHttpServer.cs:227` en main). La tarea vencía sin observarse y la respuesta no se cerraba: HTTP.sys mantiene la conexión abierta. Ahora se observa toda excepción y se corta.
2. **El bucle de aceptación se apaga.** Si `GetContextAsync` fallaba una vez (HTTP.sys puede devolver error al recibir una petición), el bucle terminaba en silencio. HTTP.sys sigue aceptando conexiones y encolando peticiones, pero nadie las toma, así que el cliente espera hasta el plazo. Ahora el bucle sigue escuchando.
3. **El puerto estaba ocupado en IPv6 por otro proceso.** En Windows el cliente va a `localhost`, que resuelve primero a `::1`, y el puerto solo se comprobaba en 127.0.0.1. Ahora se comprueba en las dos pilas.

No pude reproducirlo: en Linux no ocurre y no tengo Windows. Las tres correcciones atacan esos tres caminos. Si vuelve a pasar, `Faults` del servidor guarda la excepción que hoy se perdía; las pruebas del descargador ya lo exigen vacío, pero las de `TestHttpServerTests.cs` no lo miran (no las toqué).

## Decisiones

- **416 también para el final del rango que no cabe en `long`.** RFC 9110 permitiría recortarlo al tamaño del archivo, pero la tarea pide 416, y ningún descargador manda eso: es mejor que la anomalía se vea.
- **`Faults` público** en lugar de relanzar en `DisposeAsync`: así una prueba que provoca un fallo a propósito puede comprobarlo, y las demás exigen que esté vacío.
- **`IAsyncLifetime` + `IDisposable`** en las clases migradas. xunit 2 no llama a `IAsyncDisposable` de la clase de prueba (lo comprobé con un `Assert.Fail` provisional), y CA1001 pide `IDisposable` para el `HttpClient` y la carpeta temporal.
- **`Rodaje.TestServer` depende de `Rodaje.Paquetes`.** Lo pide la tarea: un solo firmante. La biblioteca sigue sin paquetes NuGet.

## Pendiente (fuera de mis caminos)

- `FileDownloader` no manda `If-Range` al reanudar (`src/Rodaje.Paquetes/Download/FileDownloader.cs:141-144`). Hoy el hash protege igual, como muestra `A_file_that_changed_between_cut_and_resume_is_caught`, pero con `If-Range` se ahorraría bajar una mezcla inútil. La prueba pasa con o sin él.
- `TestHttpServerTests` podría exigir `Faults` vacío al cerrar cada servidor para diagnosticar mejor un fallo como el de Windows.
- `herramientas\probar.ps1`: no lo revisé a fondo. Si recorre proyectos por la carpeta `tests\`, no corre `testing\Rodaje.TestServer\Tests` (en Windows sí lo corre `dotnet test Rodaje.slnx` de la CI).

## Pruebas (Linux, SDK 10.0.401)

| Proyecto | Antes | Después | Omitidas antes → después |
|---|---|---|---|
| `Rodaje.Paquetes.Tests` (filtro de la CI) | 385 | 392 | 2 → 2 |
| `Rodaje.TestServer.Tests` | 55 | 71 | 0 → 0 |

Las 2 omitidas son las de `CatalogoRealTests`: van contra GitHub y solo corren con `RODAJE_PRUEBAS_RED=1`. Sin filtro, `Rodaje.Paquetes.Tests` tiene además `CubeReaderTests.Lee_65_al_cubo_en_150_ms_o_menos` (`TiempoReal`), que falló una vez antes de mis cambios por tiempo (161 ms) y no tiene que ver con esta tarea. Core (1372) y Playback (65) siguen en verde con el bucle nuevo de la CI.

## Posibles choques

- `testing/Rodaje.TestServer/*` y los `packages.lock.json` de los tres proyectos: si otra rama toca esas referencias, hay que regenerar los lockfiles.
- La reorganización (`docs/reorganizacion.md`) puede mover `testing/`; entonces hay que cambiar la `ProjectReference` de `Rodaje.Paquetes.Tests` y la línea de `ci.yml`.

## Qué comprobar en Windows

- `herramientas\dotnet-local.cmd test testing\Rodaje.TestServer\Tests\Rodaje.TestServer.Tests.csproj`, varias veces: `Status_responses_carry_their_text` y `HandlerFaultTests` en verde. En particular, que con HTTP.sys el corte tras el fallo (`AbortAfterFault`) dé `HttpRequestException` enseguida.
- `herramientas\dotnet-local.cmd test tests\Rodaje.Paquetes.Tests\Rodaje.Paquetes.Tests.csproj`: las reanudaciones con `CutSignal` exactas, y `Resumes_after_an_immediate_cut` con un prefijo de entre 0 y 200 000 bytes.
- Que el Firewall de Windows siga sin preguntar (las sondas de puerto escuchan solo en 127.0.0.1 y `::1`).
