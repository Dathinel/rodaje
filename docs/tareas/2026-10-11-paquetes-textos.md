# Defectos del lector de paquetes hallados por el fuzz

Rama: `claude/paquetes-textos`. Tarea 11 de la cola.

## Hecho

Los 5 `Skip` se reprodujeron quitándolos (fallaban los 5 con las excepciones descritas en la entrega del fuzz) y ahora pasan.
- **Textos no Unicode (defectos 1 y 2).** `SafeText.HasUnreadableString` recorre una vez el documento JSON (valores y nombres de propiedad) y detecta lo que haría lanzar `InvalidOperationException` a `GetString`: UTF-8 inválido o un sustituto suelto escapado. Se llama justo tras interpretar el JSON en `ManifestReader.Read` (el error llega a `Install` como `PackageInstallException` y a `PackageLibrary.Scan` como problema de la carpeta), en `PresetReader.Parse` (`PackageContentException` con mensaje en español), en `PackageLibrary.ReadOwnItem` (el elemento propio se omite) y en `PackageCatalog.Parse` (`FormatException`, ya documentada). El catálogo y los propios no estaban en la lista de la tarea, pero tenían el mismo defecto y el arreglo es el mismo.
- **`repository` con U+FFFD (defecto 3).** `SafeText.TryParseHttpsUri` captura `UriFormatException` de `IdnHost` y devuelve `false`; el manifiesto da su error habitual de `repository`.
- **Fuzz.** Fuera los contadores y `catch` de «defecto conocido» (topes en 0: cualquier `InvalidOperationException` o `UriFormatException` hace fallar la prueba). Nueva aserción `AssertEveryDangerousNameSelected`: cada nombre peligroso se genera y está en el subconjunto que corre en Windows. Los centinelas relativos ya no se miran contra sí mismos (dependían del directorio actual): se buscan bajo el directorio actual y la carpeta temporal. Centinelas nuevos UNC (`\\srv\share\centinela-unc\x`) y `C:centinela-unidad/x`.
- `docs/formato-paquete.md`: párrafo sobre textos no Unicode y `repository`.

## Pendiente

Nada de la tarea. No quedan otros `GetString` en `src/Rodaje.Paquetes` fuera de los cuatro puntos de entrada anteriores.

## Decisiones

- Un solo recorrido previo en lugar de envolver cada `GetString`: menos código, un mensaje único y no depende de acordarse en cada lectura nueva. Cuesta un recorrido de un documento de como mucho 1 MB.
- Un texto no Unicode invalida el archivo entero, no solo el campo (no se sustituye por U+FFFD en silencio).

## Choques

Ninguno con `origin/main` en estos caminos.

## En Windows

`dotnet test tests/Rodaje.Paquetes.Tests --filter FullyQualifiedName~Fuzz` (corre el subconjunto, que ahora se comprueba que cubre todos los nombres peligrosos, incluidos UNC y `C:x`).

## Pruebas omitidas

Antes: 5 (cada una por un defecto concreto del código). Después: 0.
En Linux: 289 de 290 pasan; `CubeReaderTests.Lee_65_al_cubo_en_150_ms_o_menos` (medida de tiempo) falla con carga en esta máquina (220 ms) igual que en `origin/main` sin estos cambios, y pasa sola; no tiene relación con esta tarea.
