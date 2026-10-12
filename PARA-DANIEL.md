# Para Daniel: lo que solo tú puedes hacer

Lista corta y al día. Cada sesión de Claude te la recuerda hasta que esté hecha.

## 1. Firma de las descargas: ACTIVA desde el 12-oct; solo falta el respaldo físico

La clave está en `C:\Users\danie\.rodaje-claves\catalogo-privada.pem`, el catálogo firmado está publicado (`Dathinel/rodaje-catalogo`, release `v1`) y «Instalar/Actualizar» baja de verdad las transiciones y los filtros. **Lo único pendiente es respaldar el `.pem`** (al menos dos copias, en lugares distintos):

- **Dos pendrives** con `catalogo-privada.pem`, guardados en sitios diferentes (casa y donde un familiar).
- **Papel**: abre el `.pem` con el Bloc de notas, imprímelo y guárdalo con tus documentos importantes (es texto corto: se puede volver a teclear).
- **Opcional, más seguro**: mete el `.pem` en un `.7z` con contraseña (7-Zip, AES-256) antes de copiarlo a los pendrives. No olvides esa contraseña.
- No lo mandes por correo ni lo subas a la nube sin cifrar.

- **Si la pierdes:** las versiones ya instaladas no aceptarán catálogos nuevos; habría que sacar una versión con otra clave.
- **Si alguien la copia:** podría firmar un catálogo falso; se crea otra con `-NuevaClave -Reemplazar` y se saca una versión nueva.

## 2. Pruebas a mano

Cuando tengas un rato: `docs/pruebas-a-mano.md` (velocidad, reversa, títulos, análisis de audio, ruido, estabilizar). Lo que encuentres entra como corrección.

## 3. Preguntas abiertas (se avanzó con la recomendada)

- Subtítulos sin GPU van a ~9 min por cada 10 min de audio: ¿modelo menor solo para CPU (recomendado) o aceptar?
- Ruido: ¿quitar el paquete DirectML (~24 MB) ya que el módulo corre en CPU? (recomendado: sí).
- Medios generados (audio limpio, voz) en `<proyecto>.medios\` junto al proyecto: ¿bien así? (recomendado: sí).
- Análisis de estabilización en la caché de la app y no junto al proyecto (recomendado: caché).

## 4. Permiso de GitHub para la CI (1 min, opcional)

Claude no puede cambiar `.github/workflows/ci.yml` porque tu sesión de `gh` no tiene el permiso `workflow`. Si quieres que pueda, en una terminal normal de Windows: `gh auth refresh -h github.com -s workflow` y acepta en el navegador.
