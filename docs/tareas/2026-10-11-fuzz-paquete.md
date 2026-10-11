# Pruebas hostiles del instalador de paquetes

Rama: `claude/fuzz-paquete`.

## Hecho

`tests/Rodaje.Paquetes.Tests/Fuzz/PackageInstallerFuzzTests.cs` (solo archivos nuevos):
- **5200 casos de instalación** con semilla fija sobre un paquete válido que trae un `.cube`, un filtro, un efecto y una transición. Familias: nombres peligrosos, entradas duplicadas, paquete envuelto en carpeta, manifiesto con tipos o valores cambiados, textos de 1 MB o con marcas bidi, escapes JSON de sustituto suelto (`\ud800`, `\udc00`, `\u0000`) en manifiesto y contenido, bytes cambiados en manifiesto, contenido o zip, zip truncado, tamaños y directorio central falsos, archivos que faltan y bombas de compresión.
- **Nombres peligrosos exigidos.** `MustReject` (`..`, absolutas, UNC, `C:x`, reservados de Windows con y sin extensión, controles, bidi, `:`, `*`, más de 240 caracteres…) tiene que fallar con «ruta no permitida» o «fuera de su carpeta»; `Duplicates` (también otra caja o como carpeta), con «repite el archivo»; `Harmless` (`~`, `ñandú/ß.json`…) puede instalar. Si un nombre peligroso se instala o falla por otra causa, la prueba falla. Las rutas absolutas y con `..` apuntan a **centinelas** dentro de la carpeta temporal de la prueba (`centinela-abs`, `centinela-subir`…); la prueba comprueba que no aparecen ni ellos ni ningún otro archivo ajeno.
- Cada fallo es `PackageInstallException` con mensaje y causa nula o de E/S o zip; el resto de causas (`ArgumentException`, `NotSupportedException`…) falla la prueba, y un mensaje que no es de ninguna etapa conocida también.
- **Tras cada caso**, instalado o no, el árbol de archivos del trabajador es idéntico al vacío de partida; los instalados se desinstalan y se comprueba lo mismo, y todos sus archivos están dentro de su carpeta.
- Lo instalado se lee con los cuatro lectores y con `PackageContentStore`; `PackageContentException` exige mensaje y causa nula o `JsonException`, y el almacén dice por qué cuando no da un elemento.
- Etapas (Linux, una corrida): zip 475, manifiesto 1357, ruta 601, duplicado 421, tamaño o compresión 325, instalado con contenido válido 1017 y rechazado 1004. Pasan del zip el 91 %; el umbral es 25 %.
- **4000 casos más** solo de contenido contra `ContentReaders`: 420 válidos, 3540 rechazados con `PackageContentException`.
- **Tiempo.** En Linux los 5200 casos tardan 13–15 s (cuatro trabajadores con su carpeta; con uno, 27–30 s, por el volcado a disco de cada instalación). El tiempo **se registra, no se exige**. En Windows los 5200 casos tardaron 146 s en la CI, así que allí corre un subconjunto determinista de unos 565: el primer caso de cada nombre peligroso y 1 de cada 10 del resto (unos 3 s en Linux). Los 5200 corren en Linux.
- `PackageInstallerFuzzTests` comparte colección de xUnit con `CubeReaderTests` (una línea añadida en ese archivo): el fuzz usa todos los núcleos y, a la vez, hacía fallar su medida de 65³ en 150 ms (153 ms). Dentro de una colección no corren a la vez.
- Los textos de las pruebas fijan los finales de línea en `\n` para que los casos no cambien con el sistema.
- Omitidas: 5 pruebas nuevas con `Skip` por defectos reales (abajo). Antes de esta tarea el proyecto tenía 0 omitidas; después tiene 5, todas por defecto concreto del código.

## Defectos encontrados (no corregidos, fuera de los caminos)

Cada uno tiene una prueba de reproducción con `Skip = "Defecto: …"`, comprobada quitándole el `Skip` (falla con la excepción indicada).

1. **`PresetReader` con UTF-8 inválido** (`Content/PresetReader.cs`, `String`): `GetString` lanza `InvalidOperationException` (causa `DecoderFallbackException`) en vez de `PackageContentException`. `PresetReader_InvalidUtf8InString_…`.
2. **Sustituto suelto escapado** (`"name": "a\ud800b"`): `GetString` lanza `InvalidOperationException` («missing low surrogate») en tres sitios: `PresetReader` (`PresetReader_LoneSurrogateEscape_…`), `ManifestReader` vía `Install` (`Install_LoneSurrogateEscapeInManifest_…`) y `PackageLibrary.Scan` (`Scan_LoneSurrogateEscapeInManifest_…`: una carpeta copiada a mano tumba el escaneo al arrancar). Hallado por el revisor; el fuzz ahora lo provoca.
3. **`Install` con `repository` de dominio inválido** (`SafeText.TryParseHttpsUri`, línea 70): `Uri.IdnHost` lanza `UriFormatException`, que `Install` no envuelve. La causa es que `Encoding.UTF8` convierte un sustituto suelto en U+FFFD y eso es lo que rechaza `IdnHost`; la reproducción usa U+FFFD directamente. `Install_InvalidRepositoryHost_…`.

El fuzz cuenta los casos de los defectos 1 y 2 juntos («cadenas JSON»: 45 de 5200, 0,9 %; 40 de 4000 en el fuzz de contenido) y el 3 aparte (1), con tope del 1 %; los dos primeros se reconocen por la excepción de `System.Text.Json` y su mensaje. Al corregirlos se quitan los `Skip` y los `catch` de tolerancia.

## Pendiente / decisiones

- Las entradas con nombre vacío no se pueden generar con `ZipArchive`; no se cubren.
- El espacio en disco insuficiente y la cancelación no están en este fuzz.
- Los casos se generan primero y se reparten entre 4 trabajadores; el resultado de cada caso es determinista.

## Choques

Ninguno. Solo archivos nuevos en `tests/Rodaje.Paquetes.Tests/Fuzz/` y esta entrega, más el atributo `[Collection]` en `CubeReaderTests` (fuera de la carpeta, explicado arriba).

## En Windows

`dotnet test tests/Rodaje.Paquetes.Tests --filter FullyQualifiedName~Fuzz`. Allí corre el subconjunto. Los nombres reservados, `C:` y `:` los rechaza `SafePaths` igual que en Linux; si algún caso peligroso se instalara en Windows, `CheckExpectation` o la comparación de árboles lo señalan con el número de caso.
