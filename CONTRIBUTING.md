# Cómo contribuir a Rodaje

Gracias por tu interés. Rodaje es un editor de video nativo para Windows con un solo mantenedor; estas reglas protegen la licencia del proyecto y la calidad del código, y hacen que una contribución se pueda revisar y unir rápido.

Las contribuciones entran con el **Developer Certificate of Origin 1.1** (ver [DCO](#firma-dco)): no hay acuerdo de contribuidor ni cesión de derechos. Lo aportado queda bajo la misma licencia que Rodaje ([`LICENSE`](LICENSE), GNU GPLv3 con términos adicionales). El fondo legal está en [`docs/contribuciones.md`](docs/contribuciones.md). Al participar aceptas el [código de conducta](CODE_OF_CONDUCT.md).

## Índice

1. [Antes de empezar](#antes-de-empezar)
2. [Requisitos](#requisitos)
3. [Preparar el entorno](#preparar-el-entorno)
4. [Compilar y abrir la app](#compilar-y-abrir-la-app)
5. [Pruebas](#pruebas)
6. [Estilo de código](#estilo-de-código)
7. [Commits](#commits)
8. [Firma (DCO)](#firma-dco)
9. [Abrir un pull request](#abrir-un-pull-request)
10. [Reglas legales](#reglas-legales)
11. [Complementos y módulos de terceros](#complementos-y-módulos-de-terceros)

## Antes de empezar

- **Fallos y propuestas:** abre un [issue](https://github.com/Dathinel/rodaje/issues) con la plantilla que corresponda. Para un fallo, adjunta los pasos y, si lo hay, `app\rodaje-error.txt` (ver la [guía](docs/guia/solucion-de-problemas.md)).
- **Cambios grandes** (una función nueva, una reorganización): abre primero un issue y cuenta qué quieres hacer. Las decisiones de arquitectura y su razonamiento están en [`docs/decisiones.md`](docs/decisiones.md); un cambio que contradiga una decisión vigente se discute antes de escribir código.
- **Seguridad:** nunca en un issue público. Sigue [`SECURITY.md`](SECURITY.md).
- La arquitectura por capas está en [`docs/arquitectura/README.md`](docs/arquitectura/README.md) y el índice de toda la documentación en [`docs/README.md`](docs/README.md).

## Requisitos

| Qué | Para qué |
|---|---|
| Windows 10 (19041) o 11, x64 | La app es WinUI 3; las pruebas del núcleo, los paquetes y la reproducción también corren en Linux (así lo hace la CI) |
| Git | Clonar y contribuir |
| Windows PowerShell 5.1 | Lo usan `Probar.bat`, `Rodaje.bat` y los scripts de `build\` (viene con Windows) |
| Visual Studio Build Tools 2022 con «Desarrollo para el escritorio con C++» | Solo para publicar con Native AOT (`Rodaje.bat`, `Probar.bat aot`); compilar y probar no lo necesita |

Instalación de las Build Tools en una línea:

```
winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive"
```

No hace falta instalar .NET: el SDK va dentro del proyecto.

## Preparar el entorno

```
git clone https://github.com/Dathinel/rodaje.git
cd rodaje
powershell -NoProfile -ExecutionPolicy Bypass -File build\preparar-entorno.ps1
```

`build\preparar-entorno.ps1` descarga en `.entorno\` el SDK de .NET fijado en `global.json` (unos 280 MB), las bibliotecas LGPL de FFmpeg (unos 75 MB) y LibRaw, verifica sus huellas y deja una marca por componente. No instala nada fuera de la carpeta del proyecto. Opciones: `-Forzar` vuelve a descargar todo; `-SoloFFmpeg` solo FFmpeg. Si una descarga se corta, basta repetir: se escribe como `.part` y solo se acepta completa y con la huella correcta.

`Probar.bat` y `Rodaje.bat` preparan el entorno solos la primera vez, así que puedes saltarte este paso y hacer doble clic en uno de los dos.

- **Otro entorno:** `RODAJE_ENTORNO=<carpeta>` apunta a un `.entorno\` ya preparado (por ejemplo, el del checkout principal desde un worktree). Todas las variables están en [`docs/variables-entorno.md`](docs/variables-entorno.md).
- **Rutas con espacios y tildes:** el proyecto se desarrolla en una. Cualquier script que añadas debe funcionar con ellas (`-LiteralPath`, comillas).

## Compilar y abrir la app

| Para | Haz |
|---|---|
| Cualquier comando de `dotnet` con el SDK del proyecto | `build\dotnet-local.cmd <argumentos>` (por ejemplo, `build\dotnet-local.cmd build Rodaje.slnx`) |
| Publicar con Native AOT y abrir el editor | Doble clic en `Rodaje.bat`. Publica en `dist\Rodaje\` solo si cambió algo de `src\` y abre `dist\Rodaje\Rodaje.exe` |
| Solo publicar, sin abrir | `build\app.ps1 -SoloPublicar` |
| Abrir con un archivo | `Rodaje.bat video.mp4` o `Rodaje.bat proyecto.rodaje` |
| Medir arranque y reproducción | `build\app.ps1 -Medir 10` (`-Escenario efectos`, `velocidad`, `capas` o `rapido`) |
| Autoprueba del exe publicado | `build\app.ps1 -Autoprueba`; con `RODAJE_AUTOPRUEBA_CAPTURAS=<carpeta>` deja capturas PNG de la interfaz |
| Armar instalador, zips y notas de la versión | `build\distribuir.ps1` (en `dist\distribucion\`) |

Para depurar sin AOT, `build\dotnet-local.cmd run --project src\Rodaje.App` o abrir `Rodaje.slnx` con un IDE apuntando al SDK de `.entorno\dotnet`.

La publicación exige **cero advertencias**: `app.ps1` falla si el compilador, el recortador o AOT dejan alguna.

## Pruebas

```
Probar.bat                      todas (restaura, compila, corre y resume en español)
Probar.bat cobertura            además mide la cobertura: Rodaje.Core ≥ 99 % de líneas, Rodaje.Export ≥ 90 %
Probar.bat aot                  publica la prueba de humo del núcleo con Native AOT y la ejecuta
Probar.bat aceptacion           video y audio reales, en serie (varios minutos)
Probar.bat -Filtro Recorte      solo las pruebas cuyo nombre contiene «Recorte»
Probar.bat preparar-entorno     vuelve a descargar el SDK y FFmpeg
```

Los registros de cada corrida quedan en `TestResults\<fecha-hora>\` (se conservan diez). `RODAJE_SIN_PAUSA=1` evita la pausa final cuando se lanza desde otro programa.

**Un proyecto suelto:** `build\dotnet-local.cmd test tests\Rodaje.Core.Tests`. Hay un proyecto de pruebas por capa (`tests\Rodaje.<Capa>.Tests`) y por módulo de IA (`tests\Rodaje.Module.<Nombre>.Tests`), más `Rodaje.Acceptance.Tests` y `Rodaje.AotSmoke`. `tests\Rodaje.TestMedia` genera los medios de prueba (con el número de cuadro dibujado) y reúne en `Skip.cs` los atributos que omiten una prueba, con su motivo, cuando falta hardware.

**Categorías** (`[Trait("Categoria", "…")]`):

| Categoría | Qué es | Cuándo corre |
|---|---|---|
| (ninguna) | Pruebas unitarias y de integración rápidas | Siempre, en la CI de Windows y de Linux |
| `TiempoReal` | Miden contra el reloj de pared (reproducción, audio, procesos) | `Probar.bat` las corre en una segunda pasada, solas; la CI las excluye |
| `Rendimiento` | Tiempos de CPU con criterio para código optimizado | Fase 5 de `Probar.bat`, en Release, con `RODAJE_PRUEBAS_LARGAS=1`; la CI las excluye |
| `SoloWindows` | Dependen de códigos de error de Windows | La CI de Linux las excluye |
| `Red` | Bajan el catálogo real de complementos de GitHub | Solo con `RODAJE_PRUEBAS_RED=1` |
| `Local` | Exportan videos propios de `RODAJE_MEDIOS_LOCALES` | Solo con esa variable |

Las largas y las de hardware (NVENC, dispositivo de audio, clips de minutos) se omiten solas salvo con `RODAJE_PRUEBAS_LARGAS=1`; `build\aceptacion.ps1` la pone. Filtros con `&` desde PowerShell: `--filter '"Categoria=TiempoReal&FullyQualifiedName~Timeline"'`.

**Qué se espera de una contribución:**

- Toda lógica nueva o corregida lleva pruebas, en xUnit y, para propiedades que deben cumplirse con cualquier entrada, CsCheck. Un fallo corregido lleva una prueba que fallaba antes y pasa después.
- Si cambias el formato del proyecto (`.rodaje`), sube `schemaVersion`, escribe la migración desde la versión anterior, añade una prueba con un archivo de ejemplo de esa versión y regenera el esquema (`RODAJE_REGENERAR_ESQUEMA=1`, ver [`docs/formato-proyecto.md`](docs/formato-proyecto.md)).
- La CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) corre la solución entera en Windows y el núcleo, los paquetes y la reproducción en Linux. Un PR debe quedar en verde.

## Estilo de código

- **Idiomas.** Identificadores (tipos, métodos, propiedades, campos del formato de proyecto) y nombres de carpetas en inglés. Comentarios, documentación, mensajes al usuario, mensajes de commit y scripts que se ejecutan a mano, en español.
- **Comentarios mínimos:** solo para explicar un porqué que no sea obvio. Cada tipo público lleva un `<summary>` de una o dos frases.
- **Compilador.** C# `latest`, `Nullable` activado, `ImplicitUsings`, `AnalysisLevel` `latest-recommended` y **advertencias como errores** (`Directory.Build.props`). Una contribución compila sin advertencias.
- **Native AOT.** Nada de reflexión; JSON con el generador de código (`JsonSerializerContext`) e interop COM generado en compilación. El núcleo (`src\Rodaje.Core`) no tiene interfaz ni dependencias de Windows.
- **Capas.** Cada proyecto se apoya solo en los de abajo ([`docs/arquitectura/README.md`](docs/arquitectura/README.md)). La interfaz no toca el modelo: todo cambio es un comando del núcleo (`Edits` + `UndoStack`), para que todo se deshaga con Ctrl+Z. La lógica de las vistas que se pueda probar sin WinUI va en `Logic\` de cada vista, con sus pruebas en `tests\Rodaje.App.Tests`.
- **Dependencias.** Están fijadas en `packages.lock.json`; si añades o cambias una, actualiza el lockfile (`--locked-mode` falla a propósito si no) y las licencias (ver [Reglas legales](#reglas-legales)).
- **Documentación.** Si cambias cómo funciona algo, actualiza el documento de su capa en `docs/arquitectura/` y, si lo ve el usuario, la [guía](docs/guia/README.md). Tono de producto, frases cortas, cifras y pruebas como evidencia.

## Convención de commits

El historial de Git de Rodaje es la documentación principal de los cambios del proyecto. Seguimos una convención estricta:

- **Un commit por tarea atómica:** Un cambio lógico, autocontenido y pequeño. Nunca mezcles una refactorización de código con un cambio de lógica o corrección de defectos.
- **Idioma y redacción:** Mensajes redactados en español claro, en modo imperativo o declarativo directo.
- **Estructura del mensaje:**
  - **Primera línea (resumen):** Máximo 72 caracteres. Formato sugerido: `Área: qué cambia en presente`.
    - Áreas comunes: `Motor:`, `Audio:`, `Render:`, `Export:`, `App:`, `Núcleo:`, `Medios:`, `Pruebas:`, `Docs:`, `IA:`.
    - Si cierra o atiende una tarea o issue concreto, puede incluir la referencia: `(tarea 341)` o `(#123)`.
  - **Cuerpo (opcional pero recomendado):** Separado por una línea en blanco. Explica el **porqué** del cambio, el problema que existía antes y el enfoque técnico adoptado. Si hay consideraciones de rendimiento o concurrencia, descríbelas aquí.
  - **Pie de firma (DCO obligatorio):** La última línea debe contener `Signed-off-by:` (ver siguiente sección).

Ejemplos reales del historial:
```text
Motor: el pre-roll de un medio que cambia de pista no deja sin cuadros a la capa que lo muestra (tarea 291) (#335)

Con 4 pistas 4K HEVC solo caben 6 decodificadores en el presupuesto de VRAM, uno
por medio. Continuing solo comparte hacia atrás si el clip pasó por el tramo durante
el pre-roll (PassedLately).

Signed-off-by: Nombre <correo@ejemplo.com>
```

```text
Pruebas: opciones de sesión del motor y límites de presupuesto VRAM

Cubre límites de presupuesto VRAM, validación del caché/pre-roll y cotas de
pre-roll en TimelineSessionOptions.

Signed-off-by: Nombre <correo@ejemplo.com>
```

## Firma (DCO)

Cada commit de una contribución debe llevar la firma del [Developer Certificate of Origin 1.1](docs/DCO.txt) (https://developercertificate.org/). Al firmar certificas que tienes derecho a aportar ese código bajo la licencia del proyecto. Es una línea al final del mensaje:

```text
Signed-off-by: Tu Nombre <tu-correo@ejemplo.com>
```

Git la añade automáticamente con el modificador `-s`:

```bash
git commit -s -m "Núcleo: corrige el recorte de clips al inicio de la pista"
```

El nombre y el correo deben ser reales y coincidir con los del autor del commit. Un PR con commits sin firmar no se puede unir hasta que se firmen; si lo olvidaste, `git rebase --signoff origin/main` firma los commits de tu rama.

## Convención de Pull Requests

1. **Apertura y ramas:**
   - Haz un *fork* del repositorio y crea una rama descriptiva desde `main` (`area/descripcion-corta`, por ejemplo `export/nearest-aac` o `docs/guia-actualizada`).
   - Abre el PR como **Borrador (Draft)** mientras se completan las pruebas o la revisión.
   - Un solo tema por PR. No agrupes cambios no relacionados.
2. **Plantilla de PR:**
   - Completa todas las secciones de la [plantilla](.github/pull_request_template.md): *Qué cambia*, *Por qué*, *Cómo se probó* y marca la *Lista de comprobación*.
3. **Criterios de aceptación para unir (Merge Checklist):**
   - **CI en verde total:** Los flujos de integración continua en GitHub Actions (`ci.yml`: Windows solución completa y Linux para Core/Packages/Playback) deben pasar al 100%.
   - **Cero advertencias de compilación:** Tanto en compilación estándar como en publicación Native AOT.
   - **Zonas protegidas intocadas:** Un PR estándar **no debe modificar** archivos de infraestructura (`build/`, `.github/`), licencias (`LICENSE`, `TRADEMARK.md`), dependencias (`packages.lock.json`), ni autoría (`UpstreamAttribution.cs`). Los cambios en estas zonas requieren PRs independientes de revisión especial.
   - **Integridad de pruebas:** Las pruebas no deben debilitar aserciones ni usar `[Skip]` para esconder defectos.
4. **Estrategia de unión:**
   - Se unen mediante **Squash and Merge** conservando el mensaje descriptivo y la atribución del autor en el commit resultante sobre `main`.
   - **Nunca se utiliza `--admin`** para saltar comprobaciones obligatorias ni protecciones de rama.

## Reglas legales

- Todo el código que aportes debe ser tuyo o tener una licencia permisiva (MIT, BSD, Apache-2.0 o similar) que permita incorporarlo. **No copies, traduzcas ni adaptes código de proyectos con licencia GPL o similar** (Kdenlive, Shotcut, OpenShot, Olive…): aunque Rodaje también es GPLv3, el permiso para complementos y los términos de atribución de `LICENSE` no se pueden aplicar a código GPL ajeno, y el proyecto perdería su titular único. Se puede estudiar cómo funcionan; el código se escribe desde cero. Si dudas del origen de un fragmento, dilo en el PR.
- Nunca incluyas recursos sacados de programas comerciales (efectos, plantillas, fuentes, música, iconos) ni uses sus marcas en el código o la interfaz. Los formatos, preajustes y zonas seguras llevan nombres genéricos.
- Si añades una dependencia, anótala en [`LICENCIAS.md`](LICENCIAS.md) con su licencia verificada y, si se redistribuye con el programa, en [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
- Nunca subas claves, tokens, certificados (`*.pfx`, `*.pem`) ni archivos `.env`.
- El nombre «Rodaje» y sus logotipos siguen [`TRADEMARK.md`](TRADEMARK.md).

## Complementos y módulos de terceros

No hace falta tocar Rodaje para ampliarlo, y el **término 4 de `LICENSE`** permite que lo que se conecta por sus interfaces públicas lleve la licencia que elija su autor, también privativa o de pago.

- **Paquetes de contenido** (transiciones, filtros, LUT `.cube`, estilos de texto, zonas seguras, fuentes): un zip con `paquete.json` y sus recursos, sin código. Formato en [`docs/formato-paquete.md`](docs/formato-paquete.md); ejemplos reales en [`Dathinel/rodaje-transiciones`](https://github.com/Dathinel/rodaje-transiciones) y [`Dathinel/rodaje-filtros`](https://github.com/Dathinel/rodaje-filtros) (CC0, con su `empaquetar.ps1`). Se prueba con «Instalar desde archivo…» en Complementos o soltando el `.paquete` en la biblioteca.
- **Módulos de IA**: un programa aparte, en cualquier lenguaje, que habla con el editor por una tubería con JSON y recibe cuadros y audio por memoria compartida. Guía completa con un módulo mínimo en C# y en otro lenguaje: [`docs/protocolo-modulos.md`](docs/protocolo-modulos.md); ejemplo en `modules\Rodaje.Module.Echo`. Se empaqueta como `.paquete` con `"kind": "module"` ([Empaquetarlo](docs/protocolo-modulos.md#empaquetarlo)).
- Para que un complemento aparezca en el catálogo de la app (descarga firmada desde `Dathinel/rodaje-catalogo`, [`docs/descarga.md`](docs/descarga.md)), abre un issue con la plantilla de propuesta y el enlace a su repositorio.
