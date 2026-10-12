# Pruebas a mano para Daniel

Lo que solo Daniel puede juzgar. Se abre con `Rodaje.bat` (o `dist\Rodaje\Rodaje.exe` si ya está compilado). Al probar algo, se marca con la fecha y lo que se vio; lo que falle pasa a `CLAUDE.md` como pendiente.

## Correcciones tras la prueba del 10-oct

- [ ] **Inspector:** el ↻ se apaga en un parámetro sin cambios; restablecer sin keyframes no pregunta; con keyframes avisa con el número y Esc cancela; «Restablecer todo» (menú «⋯» de Propiedades) da el total; el ↻ de la cabecera de «Posición y tamaño» y de «Audio» restablece solo su sección; clic derecho en una fila muestra «Quitar keyframes»; Ctrl+Z deshace cada cosa de una vez.
- [ ] **Atajos:** abre en «Esencial»; ¿sirve la lista de 18?; ¿lista debajo del teclado (como está) o al lado?; recuerda la vista al volver.
- [ ] **Teclado cortado:** si vuelve a pasar, anotar vista, tamaño de ventana y escala de pantalla (100/125/150 %).
- [ ] **Línea de tiempo:** recortar clips de video y audio por ambos lados, con zoom y desplazando: nada sale del borde.
- [ ] **Ojo y candado:** todos los clips de la pista cambian a la vez, también al deshacer; ¿gusta la intensidad (0,55 oculta, 0,3 bloqueada)?; una pista bloqueada se puede ocultar y silenciar.
- [ ] **Espacio:** tras pulsar un botón de la línea de tiempo, reproduce y no abre «Archivo»; con Alt o F10 el menú sigue funcionando.
- [ ] **Dos ventanas de Rodaje:** en Complementos, «Desinstalar» en gris con el aviso; al cerrar la otra, se activa en ~2 s; un paquete ya instalado no se reinstala.
- [ ] **Confirmar:** flechas sobre un deslizador enfocado (también el zoom) lo mueven a él; Ctrl izquierdo + Alt derecho ya no lanza atajos Ctrl+Alt.

## Fase 5

**Antes:** en Rodaje → Complementos → «Instalar desde archivo», instalar `D:\Programas de uso libre\Complementos\rodaje-filtros\dist\rodaje-filtros-1.0.0.paquete` y `D:\Programas de uso libre\Complementos\rodaje-transiciones\dist\rodaje-transiciones-1.0.0.paquete` (ya generados; se regeneran con el `empaquetar.ps1` de cada carpeta).
odaje-filtros\dist
odaje-filtros-1.0.0.paquete` y `..\Complementos
odaje-transiciones\dist
odaje-transiciones-1.0.0.paquete` (ya generados; se regeneran con el `empaquetar.ps1` de cada carpeta).

- [ ] **Looks en pausa:** aplicar «Cálido» con la reproducción en pausa: el tono debe verse sin reproducir. Cada look aparece una sola vez en Filtros.
- [ ] **Dirección de los deslizamientos:** «Deslizar a la izquierda» en un corte: el clip nuevo entra desde la derecha. Probar las 8.
- [ ] **Desinstalar en vivo:** con el proyecto guardado, desinstalar `rodaje-filtros`: ¿desaparece la LUT del visor? Reabrir: aviso «Faltan paquetes» y la transición como fundido cruzado. Reinstalar: todo vuelve.
- [ ] **Capas al reproducir:** con 2 pistas de video, al reproducir o saltar ya no aparece primero Video 1 y luego Video 2.
- [ ] **Fluidez frente a CapCut:** el mismo clip 4K en 3 capas con un filtro, desenfoque y superponer, reproducido en los dos.
- [ ] **Filtros:** añadir Color y LUT y mover deslizadores reproduciendo (¿responden al instante?); keyframes y restablecer; los 13 modos de mezcla y sus nombres; «Cargar .cube…» y mover el proyecto a otra carpeta.
- [ ] **Looks:** instalar `rodaje-filtros` desde Complementos y probar los 8 (¿gustan?).
- [ ] **Transiciones:** instalar `rodaje-transiciones`; arrastrar transiciones a un corte; manijas de duración; seleccionar y Supr; inspector de la transición (tipo, duración, dirección, color); ¿0,5 s por defecto está bien?; ¿se nota el cuadro congelado cuando falta material?
- [ ] **Fundido auto:** encenderlo y hacer clic en los símbolos de la pista principal.
- [ ] **Audio:** volumen del clip con keyframes hasta +24 dB y fundidos; herramienta E en la curva (¿cómoda la escala con 0 dB a media altura?); volumen de pista arrastrando en la cabecera (¿o prefiere un deslizador?); la curva sigue a los clips al borrar con ripple o reordenar.
- [ ] **Calidad de vista previa:** ¿se nota cuando Auto baja?; ¿se entiende «Auto · ½»?
- [ ] **Biblioteca sin paquetes:** se ve bien y «Conseguir más en Complementos» lleva a Complementos.

## Fase 5c: velocidad, curvas y reversa

- [ ] **Velocidad constante:** Velocidad → deslizador a 2× y 4× frente a CapCut con el mismo clip (¿fluido?); cámara lenta a 0,25×; voz y música con y sin «Mantener tono» (a 1× el interruptor queda apagado); por encima de 8× el audio suena granuloso y el inspector avisa; Alt + arrastrar un borde estira el clip; «Congelar» (Mayús+R) inserta el cuadro y corre lo que sigue; atajos Ctrl+R, Ctrl+Alt+. y Ctrl+Alt+,; al acortar un clip el siguiente se une, al alargarlo respeta los huecos.
- [ ] **Curvas:** en Velocidad → Curva, probar los 5 preajustes reproduciendo (¿las rampas se sienten como en CapCut?); arrastrar puntos arriba y abajo y a los lados (¿cómoda la escala logarítmica y el imán en 1×?); doble clic para añadir y quitar; Ctrl+Z deshace cada arrastre de una vez; la mini curva y el nombre del preajuste en el clip, con clips cortos (el nombre del clip nunca queda tapado); Alt + borde sobre un clip con curva.
- [ ] **Reversa 1080p:** Velocidad → «Congelar e invertir» → «Invertir» en un clip 1080p con voz o música. Reproducir de principio a fin y con saltos (clic en la regla, J/K/L): ¿fluido, sin tirones al cruzar los cortes ni al empezar? ¿el sonido va hacia atrás limpio, sin chasquidos, y a 2× invertido también? Ctrl+Z quita la reversa de una vez; «◀» aparece en el clip y «◀ Invertido» en el inspector; «Mantener tono» se apaga a 1× y se enciende a 2×.
- [ ] **Reversa 4K:** lo mismo con un clip 4K: el aviso amarillo dice que se genera el proxy con su porcentaje y luego «listo» en gris; mientras se genera puede ir a saltos, ya listo debe ir fluido. ¿Se entiende el aviso? ¿Molesta?

## Fase 5b: títulos en la app

El título se ve en el visor y sale en la exportación (la autoprueba lo comprueba en el exe publicado; `ExportTitleTests` compara el MP4 con la vista previa).

- [ ] **Crear:** con el cursor sobre un clip pulsar T (y Editar → «Añadir texto»): aparece «Texto» de 5 s en morado en la pista de arriba libre, seleccionado, y el inspector salta a la pestaña Texto. Otra T en el mismo punto crea la pista «Texto» encima. ¿5 s está bien? ¿El morado se distingue del video?
- [ ] **Escribir:** teclear en la pestaña Texto (con tildes, ñ, emoji e Intro para otra línea): el clip muestra la primera línea. Ctrl+Z deshace una tanda entera, no letra a letra; una pausa de 1 s o salir del campo empieza otra. ¿Ese segundo es el corte justo?
- [ ] **Estilo:** fuente (de la lista o escribiendo una del sistema), grosor, cursiva, tamaño, color (con transparencia), alineación, interlineado, espaciado, borde, sombra y fondo. Arrastrar un deslizador es un paso del historial. ¿Falta algo que CapCut tenga a mano?
- [ ] **Biblioteca → Texto:** doble clic en «Texto» crea un título; con un título seleccionado, doble clic en un estilo de paquete lo cambia y el inspector dice «Copiado de…»; arrastrar un estilo a una pista libre lo pone donde cae.
- [ ] **Línea de tiempo:** mover, recortar y alargar sin tope (también por delante de su inicio), cortar con S; Alt + arrastrar el cuerpo o un borde no cambia nada y el globo explica por qué; Ctrl+R muestra en Velocidad que un título no tiene material.
- [ ] **Entrada y salida:** con un estilo animado, desmarcar «Animar la entrada» y pulsar «Convertir en keyframes»: los rombos aparecen en Propiedades.
- [ ] **En el visor:** el título aparece al instante al crearlo y cambia mientras se escribe o se mueve un deslizador del estilo, también en pausa. ¿Se nota algún parpadeo o retraso al teclear?
- [ ] **Asas:** con el título seleccionado, el visor lo enmarca con cuatro esquinas. Arrastrar el texto lo mueve; arrastrar una esquina lo agranda o achica desde el centro; un clic sobre otro título lo selecciona. Cada arrastre es un Ctrl+Z; Esc a mitad lo revierte. Con zoom 50 % y 100 % las asas siguen al texto. ¿Se agarra con comodidad? ¿Falta girar?
- [ ] **Fuente que falta:** escribir en Fuente una que no esté instalada: el texto se ve con la de respaldo y la pestaña Texto avisa. Exportar: el diálogo dice «Falta una fuente» y «Exportar sin él» sale con el respaldo.
- [ ] **Exportar:** un título con borde y sombra sobre un video a MP4: se ve igual que en el visor.
- [ ] **Filtros sobre un título:** la pestaña Filtros avisa de que color, LUT y viñeta no aplican; desenfoque y nitidez sí.
- [ ] **Subtítulos:** clic derecho en una cabecera → «Añadir pista de subtítulos» (o «Usar para subtítulos» en una de video): la cabecera dice «Subtítulos» y T no pone títulos ahí.

## Fase 7: silencios, ritmo y ducking

Clic derecho en un clip con sonido, o Editar, con el clip seleccionado.

- [ ] **Quitar silencios:** en una toma tuya hablando, «Quitar silencios...»: la barra muestra en rojo lo que se corta y cambia al mover umbral, silencio mínimo y margen. «Quitar» corta video y audio juntos y cierra los huecos si la casilla de ripple está marcada (empieza como el ripple automático de la línea de tiempo); Ctrl+Z lo deshace todo de una vez. ¿Los cortes suenan naturales con el margen de 100 ms? ¿Acierta el umbral de −35 dB?
- [ ] **Detectar ritmo:** en una canción, «Detectar ritmo...» dice el tempo; «Aplicar» pone un marcador por pulso y, con «Cortar el clip al ritmo», corta cada 4 pulsos (u otro). ¿Los marcadores caen en el golpe? ¿El tempo es el que conoces (en temas muy rápidos puede salir la mitad)?
- [ ] **Ducking:** selecciona la música y elige la pista de la voz: «Aplicar» pone keyframes de volumen que bajan la música antes de cada frase y la suben después. ¿−12 dB y 100 ms de anticipación suenan bien? ¿La subida de 0,5 s es lenta o rápida?

## Fase 7: estabilizar y color automático

Clic derecho en un clip de video, con el clip seleccionado.

- [ ] **Estabilizar:** en una toma tuya a mano, «Estabilizar...» analiza (la barra avanza; la segunda vez sale al instante de la caché) y dice cuánto temblor queda y cuánto se acerca la imagen. Mover la intensidad y la casilla de recorte cambia esas cifras. «Estabilizar» pone el efecto y el visor se ve quieto; Ctrl+Z lo quita. ¿Se ve natural con 50 %? ¿Se nota el acercamiento? ¿Una panorámica sigue fluida? En la pestaña Filtros, la intensidad y el recorte cambian el resultado sin volver a analizar. Exportar: se ve igual que en el visor.
- [ ] **Color automático:** en un clip oscuro o con dominante, «Color automático» ajusta exposición, contraste, temperatura y matiz del efecto de color (lo crea si no hay) en un paso; el aviso trae «Deshacer». ¿Queda natural o exagerado?
- [ ] **Balance de blancos automático:** en un clip con luz de tungsteno o muy azul, quita la dominante sin tocar el contraste. ¿Los blancos quedan blancos? ¿Pieles naturales?
## Fase 7: reducir ruido y voz con IA (modulo-audio-ia)

Antes, una vez: `powershell -NoProfile -ExecutionPolicy Bypass -File herramientas\preparar-modelos.ps1` (baja DeepFilterNet3 y Silero VAD, 10 MB). Rodaje encuentra el módulo en `modules\` si corre desde el repositorio.

- [ ] **Reducir ruido:** en una toma tuya con ruido (ventilador, calle, aire acondicionado), clic derecho → «Reducir ruido...» (también sobre el video: usa su audio enlazado). Mientras limpia, Trabajos → IA muestra el avance y se puede cancelar. Con «Escuchar», alternar «A: original» y «B: limpio»: ¿el cambio es inmediato y en el mismo punto? ¿La voz suena natural o «robótica»? Bajar «Cuánto ruido se quita» a 12–20 dB: ¿suena mejor dejando algo de ambiente? «Aplicar»: el clip pasa a «… (sin ruido)» y Ctrl+Z vuelve al original de una vez. Recortar un poco el clip después (hasta 2 s) sigue funcionando.
- [ ] **Ducking con Silero:** repetir el ducking de arriba con el módulo instalado: ¿la música baja solo cuando se habla, también con ruido de fondo o música en la pista de voz? (Sin el módulo usa la energía, como antes.)
