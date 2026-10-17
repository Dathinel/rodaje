# Instalar Rodaje

Rodaje se descarga de [Releases](https://github.com/Dathinel/rodaje/releases). No necesita .NET ni nada instalado aparte: viene compilado a código nativo.

## Requisitos

- Windows 10 versión 2004 (19041) o posterior, o Windows 11, de 64 bits (x64).
- Una GPU con DirectX 11. Si además decodifica H.264 y HEVC por hardware (NVIDIA, AMD o Intel de los últimos años), la edición va más suelta; sin ella se decodifica en la CPU.
- Para los módulos de IA pesados (subtítulos, traducción, quitar fondo), una GPU con 4 GB de VRAM o más. Sin GPU funcionan en la CPU, más despacio. Detalle en [IA local](ia-local.md).

## Tres formas de descargarlo

| Archivo | Para quién | Qué hace |
|---|---|---|
| `Rodaje-<versión>-instalador.exe` | Recomendado | Instala por usuario, sin permisos de administrador, en `%LOCALAPPDATA%\Programs\Rodaje`. Crea el acceso en Inicio, abre los `.rodaje` con Rodaje y lo añade a «Abrir con» de videos y audios |
| `Rodaje-<versión>-portable.zip` | Sin instalar | Descomprime la carpeta donde quieras y abre `Rodaje.exe`. No toca el registro; para quitarlo, borra la carpeta |
| `Rodaje-<versión>-completo.zip` | Sin conexión o con IA desde el primer minuto | El portable con los complementos, los módulos de IA y sus modelos ya dentro. Los modelos de más de 2 GiB (hoy, el de traducción) se bajan desde Complementos |

Junto a los archivos está `SHA256SUMS.txt`. Para comprobar una descarga, en PowerShell: `Get-FileHash Rodaje-1.0.1-portable.zip` y compara con la línea del archivo.

**Actualizar desde una versión anterior:** abre el instalador nuevo; instala encima de la anterior. Tus ajustes, atajos y complementos viven en `%LOCALAPPDATA%\Rodaje` y se conservan. Con el portable, descomprime la versión nueva en otra carpeta.

## El aviso de SmartScreen

Rodaje 1.0.1 va sin firma de código. La primera vez que abras el instalador o `Rodaje.exe`, Windows SmartScreen puede mostrar «Windows protegió su PC». Es el aviso que sale con cualquier programa nuevo sin certificado, no un diagnóstico del archivo:

1. Pulsa **Más información**.
2. Pulsa **Ejecutar de todas formas**.

Solo lo pide una vez por archivo. Si quieres una garantía más, comprueba el hash con `SHA256SUMS.txt` antes de abrirlo.

## Instalador paso a paso

1. Abre `Rodaje-<versión>-instalador.exe`. Se instala en tu perfil, así que no pide la contraseña de administrador.
2. Lee la licencia (GPLv3 con términos adicionales; el nombre y el logotipo no se licencian con el código).
3. Tareas opcionales: «Crear un acceso directo en el escritorio», «Abrir los proyectos .rodaje con Rodaje» y «Añadir Rodaje a «Abrir con» de videos y audios».
4. Al terminar, «Abrir Rodaje».

**Desinstalar:** Configuración → Aplicaciones → Rodaje. Pregunta «¿Borrar también los ajustes, la caché y los complementos de Rodaje?» (por defecto, no). Tus proyectos y videos no se tocan nunca.

## Portable paso a paso

1. Descomprime el zip. Dentro verás la carpeta `Rodaje` con `Rodaje.exe` y la carpeta `app`.
2. Abre `Rodaje.exe`. Es un lanzador pequeño que abre `app\Rodaje.exe` y, la primera vez, oculta la carpeta `app` para que solo se vea el ejecutable.
3. Para actualizar, reemplaza la carpeta entera. Para quitarlo, bórrala.

Si mueves solo `Rodaje.exe` sin la carpeta `app`, el lanzador avisa: «Vuelve a descomprimir Rodaje completo: la carpeta «app» tiene que quedar junto a este Rodaje.exe (puede estar oculta)».

## Dónde guarda Rodaje sus cosas

Todo lo que no es tu proyecto vive en `%LOCALAPPDATA%\Rodaje` (escribe esa ruta en el Explorador):

| Carpeta o archivo | Qué es |
|---|---|
| `ajustes.json`, `atajos.json`, `plantillas.json` | Ajustes, atajos personalizados y plantillas de proyecto |
| `Autoguardado\` | Copias automáticas para recuperar tras un cierre inesperado |
| `proxies\` | Versiones ligeras de los videos pesados para la vista previa |
| `cache\` | Miniaturas, formas de onda y shaders compilados; se puede borrar |
| `medios-generados\` | Audio sin ruido, voces, voz en off, revelados RAW y máscaras que crea la app |
| `complementos\` | Paquetes, módulos de IA, modelos y tus elementos propios |
| `fallos\` | Informes de fallo que la app escribe si se cierra de forma inesperada |
| `cola-exportaciones.json` | La cola de exportación, para reanudarla si cierras la app |

Para llevar Rodaje en una memoria USB con sus ajustes, define la variable de entorno `RODAJE_DATOS` con otra carpeta.

Los errores inesperados quedan en `app\rodaje-error.txt`, dentro de la carpeta de Rodaje (con el instalador, `%LOCALAPPDATA%\Programs\Rodaje\app`). Ver [Solución de problemas](solucion-de-problemas.md).

## Primer arranque

La ventana aparece en menos de un cuarto de segundo y la primera vez ofrece un recorrido de seis pasos (biblioteca, visor, herramientas, línea de tiempo, panel derecho y Exportar). Se puede saltar y volver a ver en **Ayuda › Recorrido inicial**. Después, la primera vez que importes, edites, exportes, entres en pantalla completa o vigiles una carpeta, sale una pista discreta; **Ayuda › Volver a mostrar las pistas** las reactiva.

Sigue con [Tu primer proyecto](primer-proyecto.md).
