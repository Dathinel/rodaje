# Para Daniel: lo que solo tú puedes hacer

Lista corta y al día. Cada sesión de Claude te la recuerda hasta que esté hecha.

## 1. Crear la firma de las descargas (5 min)

Activa «Instalar/Actualizar» desde la app. Sin ella, la app sigue con «Instalar desde archivo».

1. En la caja de mensajes de Claude Code (con el `!` delante):
   ```
   ! cd "D:\Programas de uso libre\Rodaje"; powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\firmar-indice.ps1 -NuevaClave
   ```
   - Deja la clave **privada** en `C:\Users\danie\.rodaje-claves\catalogo-privada.pem` (nunca va a GitHub).
   - Escribe la clave **pública** en `src\Rodaje.App\Services\Packages\clave-catalogo.txt` (esa sí se sube; Claude la sube).
2. **Respaldo físico** (al menos dos copias, en lugares distintos):
   - **Dos pendrives** con `catalogo-privada.pem`, guardados en sitios diferentes (casa y donde un familiar).
   - **Papel**: abre el `.pem` con el Bloc de notas, imprímelo y guárdalo con tus documentos importantes (es texto corto: se puede volver a teclear).
   - **Opcional, más seguro**: mete el `.pem` en un `.7z` con contraseña (7-Zip, AES-256) antes de copiarlo a los pendrives. No olvides esa contraseña.
   - No lo mandes por correo ni lo subas a la nube sin cifrar.
3. Dile a Claude «ya creé la firma». Claude sube la clave pública, crea el repo público `Dathinel/rodaje-catalogo` y te deja el primer catálogo listo para firmar con un comando.

- **Si la pierdes:** las versiones ya instaladas no aceptarán catálogos nuevos; habría que sacar una versión con otra clave.
- **Si alguien la copia:** podría firmar un catálogo falso; se crea otra con `-NuevaClave -Reemplazar` y se saca una versión nueva.

## 2. Pruebas a mano

Cuando tengas un rato: `docs/pruebas-a-mano.md` (velocidad, reversa, títulos, análisis de audio, ruido, estabilizar). Lo que encuentres entra como corrección.

## 3. Preguntas abiertas (se avanzó con la recomendada)

- Subtítulos sin GPU van a ~9 min por cada 10 min de audio: ¿modelo menor solo para CPU (recomendado) o aceptar?
- Ruido: ¿quitar el paquete DirectML (~24 MB) ya que el módulo corre en CPU? (recomendado: sí).
- Medios generados (audio limpio, voz) en `<proyecto>.medios\` junto al proyecto: ¿bien así? (recomendado: sí).
- Análisis de estabilización en la caché de la app y no junto al proyecto (recomendado: caché).
