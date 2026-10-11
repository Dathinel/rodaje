# Pruebas hostiles del instalador de paquetes

Rama: `claude/fuzz-paquete`.

## Hecho

`tests/Rodaje.Paquetes.Tests/Fuzz/PackageInstallerFuzzTests.cs` (solo archivos nuevos):
- **5200 casos de instalación** con semilla fija sobre un paquete válido que trae un `.cube`, un filtro, un efecto y una transición. Familias: rutas peligrosas (`..`, absolutas, UNC, `C:`, nombres reservados de Windows, controles, marcas bidi, 5000 caracteres, 400 niveles), entradas duplicadas (también en otra caja), paquete envuelto en carpeta, manifiesto con tipos cambiados o textos de 1 MB, bytes cambiados en manifiesto, contenido o zip, zip truncado, tamaños y directorio central falsos, archivos que faltan y bombas de compresión.
- Cada caso instala o lanza `PackageInstallException` con mensaje. **La causa interna solo puede ser E/S, zip (`InvalidDataException`) o JSON**; cualquier otra falla la prueba.
- **Tras cada caso**, instalado o no, el árbol de archivos del trabajador es idéntico al vacío de partida (nada queda en `.instalando-*`, nada se escribe fuera de la carpeta, se mira la raíz entera). Los instalados se desinstalan y se comprueba lo mismo, y todos sus archivos están dentro de su carpeta.
- Lo instalado se lee con los cuatro lectores y con `PackageContentStore`.
- Etapas medidas (una corrida): zip 447, manifiesto 1414, extracción/instalación rechazada 1447, instalados 1892 (contenido válido 890, rechazado 1002). Pasan del zip el 91 %; el umbral es 25 %.
- **4000 casos más** solo de contenido (`.cube`, filtro, efecto, transición) directos contra `ContentReaders`: 492 válidos, 3488 rechazados con `PackageContentException`.
- Tiempo: 13 s en Linux los 5200 casos (cuatro trabajadores con carpeta propia; con uno solo tardaba 27–30 s porque cada instalación vuelca a disco). Límite de 30 s en Linux; en Windows, 146 s en el runner de la CI (volcado a disco y antivirus), así que allí el límite es de 5 min y solo detecta cuelgues.
- Omitidas: 2 pruebas nuevas con `Skip` por defectos reales (abajo). Antes de esta tarea el proyecto tenía 0 omitidas; después tiene 2, ambas por defecto concreto del código.

## Defectos encontrados (no corregidos, fuera de los caminos)

1. **`PresetReader` con UTF-8 inválido** (`src/Rodaje.Paquetes/Content/PresetReader.cs`, `String`, línea ~533): `JsonElement.GetString` lanza `InvalidOperationException` (causa `DecoderFallbackException`) en vez de `PackageContentException`. Un filtro, efecto o transición dañado así tumba a quien lo lea (`PackageContentStore.Find*` incluido). Reproducción: `PresetReader_InvalidUtf8InString_ThrowsPackageContentException` (`Skip`). El fuzz lo cuenta aparte (~22 de 5200 y 20 de 4000) y falla si pasa del 5 %.
2. **`Install` con `repository` de dominio inválido** (`SafeText.TryParseHttpsUri`, línea 70): `Uri.IdnHost` lanza `UriFormatException` (p. ej. un sustituto UTF-16 suelto en el host) que `Install` no envuelve; el lanzador recibe una excepción que no es `PackageInstallException`. Reproducción: `Install_InvalidRepositoryHost_ThrowsPackageInstallException` (`Skip`). Aparece en 1 de 5200; el fuzz lo cuenta aparte (tope 5 %) y aun así comprueba que no queda nada en disco.

Al corregirlos se quitan los `Skip` y los dos caminos `catch` de tolerancia en el fuzz.

## Pendiente / decisiones

- Las entradas con nombre vacío no se pueden generar con `ZipArchive`; no se cubren.
- El caso de espacio en disco insuficiente y la cancelación no están en este fuzz.
- Los casos se generan primero y se reparten entre 4 trabajadores; el resultado es determinista por caso.

## Choques

Ninguno: solo archivos nuevos en `tests/Rodaje.Paquetes.Tests/Fuzz/` y esta entrega.

## En Windows

`dotnet test tests/Rodaje.Paquetes.Tests --filter FullyQualifiedName~Fuzz`. Allí los nombres reservados y `C:` se rechazan por `SafePaths`; si algún caso instala en Windows y no en Linux, el chequeo de árbol vacío lo mostrará. Comprobar que el tiempo sigue bajo 30 s.
