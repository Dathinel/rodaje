# Rodaje

Editor de video nativo para Windows, rápido y por capas, con la comodidad de los editores móviles y la precisión de un editor profesional. Se distribuye como un único ejecutable compilado con Native AOT, sin instalador y sin depender de .NET instalado.

> **Estado:** fases 1 a 4 de 8 hechas. Ya se puede importar, cortar, recortar y reproducir varias capas en tiempo real en la interfaz WinUI 3. Faltan efectos, exportación e IA.

## Cómo se usa

**Para abrir el editor:** doble clic en **`Rodaje.bat`**. También se le pueden arrastrar encima videos, que se importan y van seguidos a la pista principal, o un proyecto `.rodaje`. La primera vez compila el programa en `dist\Rodaje\` (unos minutos); después abre al instante.

| Para | Haz |
|---|---|
| Importar | Ctrl+I, o arrastra archivos a la ventana |
| Poner un medio en la línea de tiempo | Doble clic en la biblioteca (va al final de la pista principal) o arrástralo |
| Reproducir y moverte | Espacio, J/K/L, ← → cuadro a cuadro |
| Editar | S corta, Q y W recortan hasta el cursor, Supr borra sin dejar hueco en la pista principal |
| Deshacer y guardar | Ctrl+Z, Ctrl+S |
| Ver o cambiar los atajos | Menú Atajos: estilos Rodaje, Ágil y Clásico, cada uno con sus propios cambios |

**Para correr las pruebas:** doble clic en **`ABRIR.bat`**. La primera vez descarga el SDK de .NET y FFmpeg LGPL en `.entorno\` (unos 355 MB, con sus huellas verificadas) y no instala nada fuera del proyecto.

| Comando | Qué hace |
|---|---|
| `ABRIR.bat` | Todas las pruebas; las de tiempo real corren al final, solas |
| `ABRIR.bat cobertura` | Pruebas más cobertura del núcleo |
| `ABRIR.bat aot` | Compila el núcleo a código nativo y lo ejecuta de punta a punta |
| `ABRIR.bat aceptacion` | Seek exacto al cuadro y sincronía audio/video con video y audio reales (unos 9 min) |
| `ABRIR.bat preparar-entorno` | Reconstruye el entorno local desde cero |

**Requisitos:** Windows 10/11 x64, internet la primera vez y Visual Studio Build Tools con C++ (Native AOT usa su enlazador) para compilar el editor y la acción `aot`.

## Cómo funciona

- **Edición no destructiva.** Un proyecto tiene secuencias, pistas y clips. Un clip solo guarda qué archivo usa, su entrada, su salida y su posición; editar cambia esos números.
- **Tiempo exacto.** Todo instante es un entero en ticks de 1/705 600 000 s, divisible por la duración de un cuadro a 24, 25, 30, 50 y 60 fps (con las variantes NTSC) y de una muestra a 44,1 y 48 kHz. No hay error acumulado.
- **Deshacer de verdad.** Toda edición es un comando con instantánea: si falla a mitad, el proyecto queda como estaba, y un arrastre entero se deshace en un paso.
- **El audio manda.** La salida de audio es el reloj; el video lo sigue y, si el equipo no alcanza, se pierde un cuadro, nunca una muestra.
- **Varias capas en la GPU.** FFmpeg decodifica con D3D11VA sobre el mismo dispositivo que compone las capas en Direct3D 11, sin copias. Los cortes se preparan antes de llegar (pre-roll) y la memoria de video tiene un presupuesto; con proxies, las capas pesadas cuestan menos.
- **Formato abierto `.rodaje`:** JSON versionado con migraciones y [esquema publicado](schema/rodaje-proyecto.v2.schema.json). El guardado es atómico, con copia `.bak`: un apagón no corrompe el proyecto.

## Evidencias

Medido el 9 y el 10 de octubre de 2026 en un Ryzen 7 7435HS con RTX 3050 Laptop:

| Prueba | Resultado |
|---|---|
| Pruebas automáticas | **Más de 1 840 pasan, 0 fallan**; las que necesitan hardware ausente o la aceptación se omiten con su motivo |
| Reproducción en la app | **0 cuadros perdidos**; una edición se ve en pantalla en **~10 ms** (mediana) |
| Varias capas en 4K con cortes | Memoria de video de **3,7 GB a 1,4 GB** con el presupuesto |
| Salto exacto al cuadro | **2 864 de 2 864** saltos aleatorios aterrizan en el cuadro exacto, en H.264 y HEVC, por GPU y por software |
| Sincronía audio/video | **0 cuadros de error en 10 minutos**, también con el reloj de audio un 0,1 % adelantado o atrasado y con el dispositivo real |
| Cobertura del núcleo | **99,6 %** de líneas y de ramas |
| Pruebas de propiedades | 10 000 proyectos aleatorios con hasta 40 ediciones y 5 000 arrastres: el modelo sigue válido y deshacer todo lo devuelve idéntico, byte a byte |
| Native AOT | **0 advertencias** en el editor y en el núcleo |
| Revisión independiente | Fases 2 y 3: los 12 defectos encontrados están corregidos, cada uno con su prueba de regresión. La de la fase 4 está en corrección |

## Hoja de ruta

| Fase | Contenido |
|---|---|
| 1 ✔ | Modelo de línea de tiempo, edición, deshacer y formato de proyecto |
| 2 ✔ | Reproducción de un clip: FFmpeg (LGPL) con decodificación por GPU y el audio como reloj maestro |
| 3 ✔ | Varias capas, compositor en GPU (Direct3D 11), presupuesto de VRAM y proxies |
| 4 ✔ | Interfaz WinUI 3: biblioteca, reproductor, línea de tiempo, inspector, atajos y complementos |
| 5 | Efectos, LUT, modos de mezcla, filtros en el render y audio con volumen y fundidos |
| 6 | Exportación por GPU y por CPU |
| 7 | IA local: subtítulos, quitar fondo, cortes al ritmo y detección de silencios |
| 8 | Órdenes en lenguaje natural y control físico con ESP32 |

## Documentación

- [Arquitectura por capas](docs/arquitectura/README.md): núcleo, medios, audio, reproducción, motor, render y app.
- [Formato de proyecto](docs/formato-proyecto.md) y [formato de paquete](docs/formato-paquete.md)
- [Decisiones](docs/decisiones.md) y [propuesta de módulos](docs/propuesta-modulos.md)
- [Variables de entorno](docs/variables-entorno.md)
- [Fondo legal](docs/legal.md)

## Licencia

MIT. Las dependencias y sus licencias están en [LICENCIAS.md](LICENCIAS.md). Todo el código es propio: los editores de código abierto se estudiaron solo a nivel de diseño, y no se usan recursos ni marcas de productos comerciales.
