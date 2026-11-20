# Progreso visible en toda operación larga (B9b)

**Requisito (Daniel, 20-nov):** en cualquier operación larga tiene que haber algo a la vista que diga que no se quedó trabada, y debe ser una **barra con porcentaje**, no una rueda (una rueda no dice si avanza). Si el porcentaje no se conoce: **fases** («Paso 2 de 5: …») o el **último avance** («Sigue trabajando; último avance hace 4 s»), con el **tiempo restante** cuando se pueda estimar y **Cancelar** cuando la operación lo admite.

Este documento tiene dos partes: el componente común y cómo usarlo (primero, para que la red de la colaboración lo use desde el principio) y la auditoría de lo que hay hoy.

## 1. El componente común

Dos piezas, sin dependencias nuevas:

| Pieza | Dónde | Qué es |
|---|---|---|
| `OperationProgress` | `src/Rodaje.App/Services/Operations/OperationProgress.cs` | El modelo, sin WinUI y seguro entre hilos. Quien trabaja informa; la interfaz lee `Snapshot()`. |
| `OperationProgressView` | `src/Rodaje.App/Views/Shared/OperationProgressView.cs` | La vista (`UserControl`): barra, porcentaje, línea de fase y tiempo restante, aviso de sin avance y botón Cancelar. |

### Lo que informa quien trabaja (`OperationProgress`)

```csharp
var progress = new OperationProgress();            // puede crearse en cualquier hilo
progress.SetCancellable(true);                     // si hay botón Cancelar

progress.SetPhase(1, 3, "Buscando al anfitrión");  // «Paso 1 de 3: Buscando al anfitrión»
progress.Report(null, "Probando conexión directa");// sin porcentaje: barra en movimiento + «lleva 8 s»

progress.SetPhase(2, 3, "Recibiendo el proyecto");
progress.Report(OperationProgress.PhaseFraction(2, 3, received / (double)total),
                $"{received / 1_000_000} de {total / 1_000_000} MB");   // fracción del total, con el paso 2 de 3

progress.Pulse();                                  // «sigo vivo» sin cambiar nada (llegó un paquete)
progress.Complete();                               // al terminar
```

- `Report(fraction, message)`: de 0 a 1, o `null` si no se sabe. Se puede llamar cada cuadro o cada paquete: solo guarda el último valor (no encola nada en la interfaz).
- `SetPhase(k, n, texto)`: «Paso k de n: texto» (con `n` = 1 solo muestra el texto). `PhaseFraction(k, n, dentro)` da la fracción del total cuando cada paso informa la suya.
- `Pulse()`: para lo que no puede dar porcentaje pero sabe que sigue vivo (un archivo que se sondea, un paquete de red). Reinicia el «último avance».
- `SetCancellable(true)` y `MarkCancelling()`: el botón sale y, al pedir cancelar, la vista dice «Cancelando…» con la barra en movimiento y sin botón hasta que termine. Lo que se informe después no lo tapa.
- `AsProgress()`: un `IProgress<double>` para lo que ya informa así (descargas, recorridos de archivos).

**Avance** es que suba la fracción, que cambie la fase o el mensaje, o un `Pulse()`. Informar lo mismo otra vez no cuenta: si lleva 5 s (`OperationProgress.DefaultStallAfter`) sin avance, la vista añade «Sigue trabajando; último avance hace N s» y retira el tiempo restante (la estimación ya no vale).

**Tiempo restante:** media móvil exponencial del ritmo (fracción por segundo, factor 0,3, muestras de al menos 0,5 s). No se muestra antes de 3 s ni con menos del 2 % hecho, ni cuando está sin avance; si la fracción retrocede (empieza otra cosa) el ritmo se reinicia. Se redondea: «queda poco», «quedan unos 40 s», «quedan unos 3 min», «quedan unas 2 h 05 min».

### Lo que dibuja la vista (`OperationProgressView`)

```csharp
var view = new OperationProgressView { Operation = progress, OnCancel = () => { cts.Cancel(); progress.MarkCancelling(); } };
panel.Children.Add(view);   // en un diálogo, la barra de estado, una fila del panel Trabajos…
```

- Barra con porcentaje a la derecha; sin fracción, barra en movimiento y, debajo, la fase o el mensaje y cuánto lleva.
- Consulta el modelo 4 veces por segundo mientras está a la vista (temporizador de 250 ms que se para al salir de pantalla) y **solo toca los controles que cambiaron**: no parpadea y no cuesta cuadros aunque se informe cientos de veces por segundo.
- Colores y estilos del tema (`CaptionTextStyle`, `TextMutedBrush`, `ProgressBar` del sistema): sirve en Grafito, claro y alto contraste. El nombre accesible de la barra es «Progreso de la operación» y el botón «Cancelar la operación».
- Sin `Operation`, se oculta (no ocupa sitio).

### Ejemplo para la red de la colaboración (B11 y B13)

Enviar o recibir el proyecto (tamaño conocido) y conectar (tamaño desconocido):

```csharp
OperationProgress p = new();
p.SetCancellable(true);
p.SetPhase(1, 3, "Conectando con el anfitrión");
// … cada intento de conexión, cada ronda de ICE:
p.Report(null, "Probando la conexión directa"); // «Paso 1 de 3: Conectando con el anfitrión · Probando la conexión directa · lleva 6 s»
// si pasan 5 s sin intento nuevo: «Sigue trabajando; último avance hace 5 s»

p.SetPhase(2, 3, "Recibiendo el proyecto");
await foreach (var chunk in receive) { received += chunk.Length; p.Report(OperationProgress.PhaseFraction(2, 3, received / (double)total)); }

p.SetPhase(3, 3, "Comprobando las huellas");
// …
p.Complete();
```

Reglas para la red: informar siempre en la capa que lee del socket (no en la interfaz); no hace falta un hilo ni un temporizador propios. Si no hay tamaño total, usar `Report(null, …)` con un mensaje que cambie (cada archivo, cada bloque) o `Pulse()` por cada paquete recibido: así, si el otro equipo calla, a los 5 s aparece «Sigue trabajando; último avance hace 5 s» en lugar de una barra que parece avanzar.

### Cómo se engancha lo que ya existe

`LongActionService` (acciones largas de la barra de avisos) ya lleva un avance con porcentaje y Cancelar; en el PR siguiente su `LongActionScope` lleva un `OperationProgress` (`scope.Operation`) y la barra de avisos usa `OperationProgressView`, de modo que todas las acciones que lo usan (color automático, silencios, subtítulos, estabilizar, texto a voz…) ganan fases, tiempo restante y «último avance» sin cambiar sus llamadas (`scope.Report(…)`, `scope.Progress`). Los diálogos con su propia barra, las descargas de paquetes, los módulos de IA y los proxies pasan al componente en PR pequeños (ver el estado en la sección 2).

## 2. Auditoría (20-nov, lectura de Devin revisada en el código)

«Antes» es lo que había; «Ahora» lo que queda tras B9b (PR en la sección 3). **Cancelar**: sí real (corta el trabajo), parcial (se cancela al cerrar el diálogo) o no.

| Operación | Antes | Ahora | Cancelar |
|---|---|---|---|
| Abrir proyecto | nada (solo `BeginBusy`) | barra de avisos con «Paso k de 2» tras 0,6 s; sin porcentaje (leer el JSON no lo da) | no |
| Guardar | nada | sin cambio: dura milisegundos | no |
| Importar medios (biblioteca, soltar, carpetas vigiladas, CinemaDNG) | nada | «Leyendo los medios: k de n», porcentaje, tiempo restante, Cancelar (tras 0,6 s) | sí |
| Importar / exportar línea de tiempo (EDL, FCPXML, OTIO) | nada | importar: porcentaje de medios sondeados y Cancelar; exportar: «Escribiendo…» sin porcentaje | importar sí |
| Exportar video (cola) | barra con %; indeterminada sin texto en preparar, medir, mezclar y cerrar | vista común: fases por nombre, fps, tiempo restante, «último avance», «Paso k de n» en varias pasadas | sí (mata `ffmpeg`) |
| Render en caché | barra con % | vista común (tiempo restante, «último avance») | sí |
| Análisis: silencios, ritmo, ducking, estabilizar | barra local con % y barra de avisos | igual, más tiempo restante y «último avance» en la barra de avisos | parcial (cierra el diálogo) |
| Color automático, tracking, quitar fondo, reencuadre | barra con % y Cancelar | igual, más tiempo restante y «último avance» | sí |
| Sincronizar por el sonido, multicámara | barra con % | igual, más tiempo restante | no (pendiente: no hay token) |
| Reducir ruido, subtítulos automáticos, transcribir | barra con % del módulo en el diálogo; el restante solo en Trabajos | vista común en el diálogo (arranque del módulo, etapa, %, restante, «último avance») y en Trabajos | parcial; real en Trabajos |
| Traducir pista, texto a voz (y cargar voces) | **rueda** | vista común (barra con % del módulo) en el diálogo y en la barra de avisos | parcial; real en Trabajos |
| Importar CinemaDNG | barra de avisos indeterminada | % del módulo en la barra de avisos | sí |
| Reenlazar medios («Buscar en carpeta») | **rueda** + conteo | barra en movimiento con lo que lleva y «último avance» (el total no se conoce) | sí |
| Grabar voz en off | cuenta atrás y vúmetro | sin cambio: una grabación no tiene total | «Detener» |
| Exportar fotograma | texto con Cancelar | sin cambio: dura menos de un segundo | sí |
| Descargar e instalar paquetes y modelos | barra con % y etapa | más «quedan unos N s» y «Sigue trabajando; último avance hace N s» | sí |
| Instalar un `.paquete` local | nada | «Instalando el paquete» en la barra de avisos | no |
| Generar proxies | barra con % en Trabajos; solo texto en la biblioteca | vista común con tiempo restante y «último avance»; «En cola» sin aviso de sin avance | sí |
| Botón «Trabajos» | **rueda** sobre el icono | barra fina (con % si es un solo trabajo) | – |
| Red de la colaboración (B11, B13) | no existe aún | usar `OperationProgress` desde el principio (sección 1) | según la operación |

No existen como operación propia: detectar escenas, flujo óptico (es un paso del render), render separado de exportar. Sin indicador por ser instantáneas: plantillas, escaneo de salud de la biblioteca, miniaturas y ondas (aparecen al pintarse), comprobar actualizaciones, autoguardado.

## 3. Estado de los PR

Ver «Registro» al final; se actualiza en cada PR que cierra una fila.
