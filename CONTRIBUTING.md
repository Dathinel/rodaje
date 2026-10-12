# Cómo contribuir a Rodaje

> **Hoy no se aceptan pull requests externos:** antes hace falta un acuerdo de contribuidor ([`docs/contribuciones.md`](docs/contribuciones.md)). Los issues con errores, dudas y propuestas sí son bienvenidos. Las reglas de abajo valdrán cuando se abran las contribuciones.

Gracias por tu interés en Rodaje. Es un editor de video nativo para Windows que construye una sola persona, así que las contribuciones son bienvenidas, pero siguen algunas reglas que protegen la licencia del proyecto y la calidad del código.

## Antes de empezar

Para cambios grandes, como una función nueva o una reorganización del código, abre primero un issue y explica lo que quieres hacer. Así se puede discutir el enfoque antes de que inviertas tiempo en algo que choque con una decisión ya tomada. Las decisiones de arquitectura y su razonamiento están en `docs/decisiones.md`, y conviene leerlas antes de proponer cambios de fondo.

## Preparar el entorno

Rodaje no depende de nada instalado globalmente salvo Git. La primera vez, el script `build\preparar-entorno.ps1` descarga el SDK de .NET fijado en `global.json` dentro de la carpeta `.entorno\` del proyecto y verifica su huella SHA-512. Después, `build\dotnet-local.cmd` ejecuta ese SDK local; por ejemplo, `build\dotnet-local.cmd test` compila y corre todas las pruebas; `Probar.bat` hace lo mismo con un resumen en español y prepara el entorno si falta. La ruta del proyecto puede contener espacios y tildes, así que cualquier script que añadas debe manejarlas.

## Reglas del código

Los identificadores (tipos, métodos, propiedades y campos del formato de proyecto) se escriben en inglés. Los comentarios, la documentación, los mensajes para el usuario y los mensajes de commit se escriben en español.

El núcleo (`src\Rodaje.Core`) no tiene interfaz y debe seguir siendo compatible con Native AOT: nada de reflexión, y serialización JSON solo mediante el generador de código. Los avisos del compilador se tratan como errores, así que una contribución debe compilar sin advertencias.

Toda lógica nueva o corregida lleva pruebas. Las pruebas viven en `tests\` y usan xUnit y, para las propiedades que deben cumplirse con cualquier entrada, CsCheck. Si cambias el formato del archivo de proyecto, debes subir `schemaVersion`, escribir la migración desde la versión anterior y añadir pruebas con un archivo de ejemplo de esa versión.

Haz commits pequeños y con un propósito claro, con un mensaje en español que explique qué cambia y por qué.

## Reglas legales

Todo el código que aportes debe ser tuyo o tener una licencia permisiva (MIT, BSD, Apache-2.0 o similar) que permita incorporarlo a Rodaje. No copies, traduzcas ni adaptes código de proyectos con licencia GPL o similar, como Kdenlive, Shotcut, OpenShot u Olive: aunque Rodaje también es GPLv3, el permiso para complementos y los términos de atribución de `LICENSE` no se pueden aplicar a código GPL ajeno, y Daniel perdería la titularidad única. Se puede estudiar cómo funcionan, pero el código debe escribirse desde cero. Si tienes dudas sobre el origen de un fragmento, dilo en el pull request.

Nunca incluyas recursos extraídos de programas comerciales (efectos, plantillas, fuentes, música, íconos) ni uses sus marcas en el código o la interfaz. Si añades una dependencia, actualiza `LICENCIAS.md` con su licencia verificada y, si se redistribuye con el programa, `THIRD-PARTY-NOTICES.txt`. Nunca subas claves, tokens ni archivos `.env`.

## Certificado de Origen del Desarrollador (DCO)

Cada commit de una contribución debe llevar la firma del Developer Certificate of Origin, versión 1.1 (https://developercertificate.org/). Al firmar, declaras que tienes derecho a enviar ese código bajo la licencia del proyecto. La firma es una línea al final del mensaje del commit:

```
Signed-off-by: Tu Nombre <tu-correo@ejemplo.com>
```

Git la añade automáticamente si haces el commit con la opción `-s`:

```
git commit -s -m "Corrige el recorte de clips al inicio de la pista"
```

El nombre y el correo deben ser reales y coincidir con los del autor del commit. Los pull requests con commits sin firmar no se pueden aceptar hasta que se firmen; si olvidaste firmar, puedes corregirlo con `git rebase --signoff` sobre tu rama.

## Licencia de las contribuciones

Rodaje se distribuye bajo la GNU GPLv3 con términos adicionales de atribución y un permiso adicional para complementos (`LICENSE`); el nombre y los logotipos siguen `TRADEMARK.md`. Hoy no se aceptan contribuciones externas: antes de aceptarlas hará falta un acuerdo de contribuidor (ver `docs/contribuciones.md`).
