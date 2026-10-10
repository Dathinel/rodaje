# Rodaje

Editor de video nativo para Windows, rápido y por capas, con la comodidad de los editores móviles y la precisión de un editor profesional. Se distribuye como un único ejecutable compilado con Native AOT, sin instalador y sin depender de .NET instalado.

> **Estado:** fases 1 y 2 de 8 completadas. El núcleo de edición y la reproducción de un clip (GPU, audio como reloj maestro y visor de prueba) están terminados y probados. La interfaz de edición y la exportación vienen en las fases siguientes.

## Cómo se usa

**Para abrir el editor:** doble clic en **`Rodaje.bat`**, o arrastra encima videos (se importan y van seguidos a la pista principal) o un `.rodaje`. La primera vez compila el programa (unos minutos); después abre al instante. Importa con Ctrl+I o arrastrando a la ventana, y haz doble clic en un medio para ponerlo al final de la pista principal; Espacio reproduce o pausa, S corta, Ctrl+Z deshace y Ctrl+S guarda. El visor de la fase 3 sigue en `ABRIR.bat visor` (teclas en `docs/prueba-visor.md`).

**Para correr las pruebas:** 

Doble clic en **`ABRIR.bat`**. La primera vez descarga el SDK de .NET en `.entorno\` (unos 280 MB, con su huella SHA-512 verificada) y no instala nada fuera del proyecto.

| Comando | Qué hace |
|---|---|
| `ABRIR.bat` | Ejecuta todas las pruebas |
| `ABRIR.bat cobertura` | Pruebas más cobertura de código |
| `ABRIR.bat aot` | Compila el núcleo a código nativo y lo ejecuta de punta a punta |
| `ABRIR.bat visor video.mp4 [otro.mp4 ...]` | Abre el visor de prueba con uno o varios videos como capas |
| `ABRIR.bat aceptacion` | Criterios de la fase 2 con video y audio reales: seek exacto al cuadro y sincronía audio/video en 10 minutos (varios minutos) |
| `ABRIR.bat preparar-entorno` | Reconstruye el entorno local desde cero |

**Requisitos:**
- Windows 10/11 x64.
- Conexión a internet la primera vez.
- Visual Studio Build Tools con C++, solo para la acción `aot`.

## Cómo funciona

- **Edición no destructiva.** Un proyecto contiene secuencias, las secuencias contienen pistas y las pistas contienen clips. Un clip no guarda video: guarda una referencia al archivo de origen, su punto de entrada, su punto de salida y su posición en la línea de tiempo. Editar solo cambia esos números.
- **Tiempo exacto.** Todo instante es un entero en ticks de 1/705 600 000 s, divisible entre la duración de un cuadro a 24, 25, 30, 50 y 60 fps (incluidas las variantes NTSC) y entre la de una muestra a 44,1 y 48 kHz. No hay error acumulado al sumar ni al restar.
- **Operaciones de línea de tiempo:**
  - Cortar y recortar, incluido el recorte ripple.
  - Roll, slip y slide.
  - Mover, en modo estricto, sobrescribir o insertar.
  - Insertar, sobrescribir y añadir al final.
  - Levantar, borrado ripple y cerrar hueco.
  - Gestión de pistas y enlace de video con audio.

  Las pistas bloqueadas son intocables.
- **Deshacer y rehacer** con el patrón comando. Cada edición guarda una instantánea y, si falla a mitad, el proyecto vuelve solo a como estaba. Los arrastres se funden en un único paso de deshacer.
- **Formato abierto `.rodaje`:** JSON versionado con migraciones y [esquema publicado](schema/rodaje-proyecto.v2.schema.json), pensado para que otros programas lo lean y lo escriban. El guardado es atómico, con copia `.bak`, así que un apagón no corrompe el proyecto.

## Evidencias

Medido el 9 de octubre de 2026 en un Ryzen 7 7435HS:

| Prueba | Resultado |
|---|---|
| Pruebas automáticas | **1 016 pasan, 0 fallan** (9 largas se reservan para la aceptación) |
| Salto exacto al cuadro (fase 2) | **2 864 de 2 864** saltos aleatorios aterrizan en el cuadro exacto, en H.264 y HEVC, por GPU y por software |
| Sincronía audio/video (fase 2) | **0 cuadros de error en 10 minutos**, con el reloj de audio un 0,1 % adelantado o atrasado; en tiempo real con el dispositivo de audio, 10 minutos completos con 0 cuadros de error, 0 tarde y 0 perdidos |
| Reloj de audio | Jitter máximo 0,7 ms frente al reloj del sistema |
| Cobertura del núcleo | **99,6 % de líneas y 99,6 % de ramas** |
| Pruebas de propiedades | 10 000 proyectos aleatorios con hasta 40 ediciones cada uno, más 5 000 arrastres. Tras cada paso el modelo sigue siendo válido, las pistas bloqueadas no cambian y deshacer todo devuelve el proyecto idéntico, byte a byte |
| Native AOT | Ejecutable de **3,39 MB**, **0 advertencias**; arranque más recorrido completo del núcleo en **117 ms** |
| Revisión independiente | Un revisor distinto del que escribió el código encontró 5 defectos. Los 5 están corregidos y cada uno tiene su prueba de regresión |

## Hoja de ruta

| Fase | Contenido |
|---|---|
| 1 ✔ | Modelo de línea de tiempo, edición, deshacer y formato de proyecto |
| 2 ✔ | Reproducción de un clip: FFmpeg (LGPL) con decodificación por GPU y el audio como reloj maestro |
| 3 | Varias capas, compositor en GPU (Direct3D 11) y proxies |
| 4 | Interfaz de línea de tiempo en WinUI 3 |
| 5 | Efectos, modos de mezcla y keyframes |
| 6 | Exportación con NVENC y alternativa por software |
| 7 | IA local: subtítulos con Whisper, quitar fondo, cortes al ritmo y detección de silencios |
| 8 | Órdenes en lenguaje natural y control físico con ESP32 |

## Documentación

- [Arquitectura por capas](docs/arquitectura/README.md): núcleo, medios, audio, reproducción, motor, render y app, con diagramas y recorrido del código.
- [Variables de entorno](docs/variables-entorno.md)
- [Formato de proyecto](docs/formato-proyecto.md)
- [Formato de paquete](docs/formato-paquete.md): complementos `.paquete` y módulos de IA
- [Decisiones](docs/decisiones.md)
- [Investigación de editores abiertos](docs/archivo/investigacion.md)
- [Fondo legal](docs/legal.md)
- [Propuesta de módulos: núcleo, contenido e IA](docs/propuesta-modulos.md)

## Licencia

MIT. Las dependencias y sus licencias están en [LICENCIAS.md](LICENCIAS.md). Todo el código es propio: los editores de código abierto se estudiaron solo a nivel de diseño, y no se usan recursos ni marcas de productos comerciales.
