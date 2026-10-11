# Pruebas a mano para Daniel

Lo que solo Daniel puede juzgar. Se abre con `Rodaje.bat` (o `dist\Rodaje\Rodaje.exe` si ya está compilado). Al probar algo, se marca con la fecha y lo que se vio; lo que falle pasa a `CLAUDE.md` como pendiente.

## Correcciones tras la prueba del 10-oct

- [ ] **Inspector:** el ↻ se apaga en un parámetro sin cambios; restablecer sin keyframes no pregunta; con keyframes avisa con el número y Esc cancela; «Restablecer todo» da el total; clic derecho en una fila muestra «Quitar keyframes»; Ctrl+Z deshace cada cosa de una vez.
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
