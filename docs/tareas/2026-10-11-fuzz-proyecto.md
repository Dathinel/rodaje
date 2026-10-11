# Pruebas hostiles del lector de proyectos `.rodaje`

Rama: `claude/fuzz-proyecto`.

## Hecho

`tests/Rodaje.Core.Tests/Fuzz/ProjectReaderFuzzTests.cs` (24 casos de xUnit, unos 12 000 textos por corrida):
- Semillas v1, v2 y v3. La v3 es el ejemplo versionado; v1 y v2 se obtienen quitando de ella lo que esos formatos no tenían (`markers`/`regions`, `effects`/`transitions` de pista, `options`).
- 6000 mutaciones de bytes (cambiar, insertar, borrar, truncar, sustituir por signos JSON) con semilla fija.
- 6000 mutaciones estructurales sobre el árbol JSON: tipos cambiados, números enormes, negativos, `NaN`/`Infinity` como texto, racionales raros, ids duplicados y referencias cruzadas, listas desordenadas (keyframes, clips), elementos nulos, campos nuevos.
- Entradas escritas a mano: `schemaVersion` ausente, futuro, negativo o enorme, campos repetidos, raíz que no es objeto, listas con `null`.
- Cadenas de 1 MB, anidamiento de 5000 niveles, truncados cada 97 bytes y bytes no válidos.
- Cada caso debe abrir o lanzar `ProjectFormatException` con mensaje. Si abre, pasa `ModelValidator` y guardar → abrir → guardar da el mismo texto.
- El conjunto corre en menos de 30 s (la prueba lo exige); en Linux tarda unos 6 s.

## Resultado

No se encontró ningún defecto: ningún caso lanzó otra excepción, se colgó o dejó un proyecto que no pasara el validador. No hay pruebas con `Skip`.

## Pendiente / decisiones

- Las semillas v1 y v2 son derivadas del v3, no archivos de verdad de esas versiones; si Daniel conserva proyectos antiguos reales, conviene añadirlos como ejemplos.
- La comparación es sobre el texto guardado (`Serialize`), que es el formato canónico del lector.

## Choques

Ninguno: solo archivos nuevos en `tests/Rodaje.Core.Tests/Fuzz/` y esta entrega.

## En Windows

Nada específico; `dotnet test tests/Rodaje.Core.Tests --filter FullyQualifiedName~Fuzz`.
