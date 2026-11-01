# Audio

Volumen, mezclador, sonoridad, efectos (ecualizador, compresor, puerta, de-esser, reverb, delay, saturación, tono, limitador), ducking, voz en off y las herramientas que limpian o cortan el sonido. El video sigue siempre al reloj del audio: si el equipo no llega, se pierde un cuadro, nunca una muestra.

## Volumen

- **Por clip:** Propiedades › sección **Audio**: «Volumen» de −60 (silencio) a +24 dB, con keyframes; «Fundidos»: «Entrada» y «Salida» en segundos (hasta la mitad del clip) y **Curva**: potencia constante (la de siempre: seno y coseno, suena pareja), lineal (recta en amplitud) o exponencial (recta en decibelios, se apaga más tarde). El botón de la cabecera restablece volumen y fundidos.
- **Asas de fundido en el clip:** cada clip de audio lleva dos cuadritos en el borde de arriba, uno en cada extremo. Arrastra el de la izquierda hacia adentro para el fundido de entrada y el de la derecha para el de salida; un globo dice los segundos («Entrada 0,5 s») y el clip dibuja la zona apagada con la curva elegida. Cada arrastre es un paso del historial (Ctrl+Z lo deshace entero). En el menú del clip, **Fundido de entrada hasta el cursor** y **Fundido de salida desde el cursor** ponen el fundido del borde del clip al cursor en los clips de audio seleccionados (o en el audio enlazado al video elegido), cada uno con sus segundos, en un paso; son acciones sin tecla de fábrica.
- **Por pista:** en la cabecera de la pista de audio, arrastra el texto «Volumen 0,0 dB» a los lados (0,25 dB por píxel; con Mayús, 0,05); doble clic vuelve a 0 dB. Clic derecho en la cabecera: **Preajuste de audio**, **Compresor de la pista…** y **Grabar voz en off aquí…**.
- **Envolvente:** con la herramienta de envolvente (**E**) sobre un clip de audio, un clic pone un punto y lo arrastra, agarrar un punto lo mueve sin pasar a sus vecinos y clic derecho lo quita. La altura es lineal en amplitud: 0 dB a media altura, +6 dB arriba y silencio abajo. El interruptor **bloquear envolventes** de la barra evita tocarlas sin querer.
- **Silenciar y solo desde la cabecera:** el altavoz silencia la pista y la **S** la pone en solo (con algún solo puesto suenan solo las pistas con solo, como en el mezclador; sus clips se ven velados en las que callan). **Ctrl+clic** en la S es solo exclusivo: esa pista queda como única con solo, y otro Ctrl+clic quita todos. Lo mismo en el menú de la cabecera (**Solo** y **Solo exclusivo**) y, para la pista cuya cabecera está elegida, con las acciones «Solo de la pista elegida», «Silenciar u ocultar la pista elegida» y «Bloquear la pista elegida» (sin tecla de fábrica; se asignan en el panel de atajos). Cada cambio es un paso del historial y se guarda con el proyecto.

## Mezclador

**Ver › Mezclador** (Alt+M) cambia la línea de tiempo por el mezclador; otra vez Alt+M vuelve. Hay una tira por pista de audio, una por bus y, a la derecha, la de la mezcla.

- **Fader:** volumen de −∞ a +12 dB con la escala de una mesa (más recorrido cerca de 0 dB). Arrastra (Mayús: fino), usa la rueda o las flechas (0,5 dB) y doble clic vuelve a 0 dB. Va **después** de las inserciones: bajar la voz no cambia lo que hace su compresor. Es el mismo volumen que el de la cabecera de la pista.
- **Paneo y balance:** la barra de arriba es el paneo (ley de −3 dB: una voz mono al centro suena igual que antes y movida a un lado no sube ni baja de volumen); la de abajo, el balance (solo baja el lado contrario). Doble clic: al centro.
- **M, S, R y T:** silenciar, **solo** (Ctrl+clic: solo exclusivo), armar para grabar (**Grabar voz** propone esa pista) y automatización **tocar**.
- **Medidor:** pico y RMS de lo que sale del fader, con el pico retenido y el aviso de saturación (clic para apagarlo). La tira de la mezcla mide el pico verdadero y muestra la sonoridad corta e integrada (LUFS) y el mayor pico (dBTP); clic sobre la lectura para volver a empezar.
- **Inserciones:** los efectos de la pista, de arriba abajo. el **+** de «Inserciones» añade uno y abre su editor; clic en uno lo abre; arrastra para cambiar el orden; la casilla lo activa o desactiva; **A/B** lo escucha sin él (solo la vista previa: no cambia el proyecto ni la exportación). Clic derecho: **Preajuste**, **Abrir en Dinámica** (el compresor), activar o quitar.
- **Salida y envíos:** arriba eliges adónde sale la pista (la mezcla o un bus). El **+** de «Envíos» manda además una copia a un bus (por ejemplo una reverb), con su nivel y **Pre** (antes del fader: lo que se manda no depende del volumen de la pista). Clic derecho en un envío para quitarlo.
- **Buses:** **Añadir bus › Voces, Música y Efectos** crea las tres submezclas de una vez. Un bus suma lo que le llega, pasa por sus inserciones y su fader y va a la mezcla. Clic derecho en su nombre: renombrar o quitar (las pistas que salían a él vuelven a la mezcla).
- **Solo:** con algún solo puesto suenan solo las pistas con solo, las que salen a un bus con solo y los buses que reciben de ellas. Una pista que hace de sidechain sigue disparando su compresor aunque un solo la calle.

### Automatización

El volumen y el paneo de una pista o un bus admiten keyframes (en el tiempo de la secuencia). Al reproducir, los faders los siguen («· A» junto al valor). Para escribirlos en vivo, enciende **T** en la tira y, mientras suena, mueve el fader o el paneo: lo que hagas queda en keyframes (solo los puntos que hacen falta) y al soltar la curva vuelve a la de antes en 0,1 s. Todo el pase es un paso del historial.

## Efectos del mezclador

Se añaden desde la tira (el **+** de «Inserciones») o, en un clip, en **Filtros**. Todos suenan igual en la vista previa y en la exportación y traen preajustes.

- **Ecualizador** (paramétrico de 5 bandas) y **Ecualizador gráfico** (10 bandas de una octava, de 31 Hz a 16 kHz, ±12 dB), con su curva.
- **Compresor:** ver la pestaña Dinámica.
- **Puerta de ruido:** calla lo que queda por debajo del umbral (relación 20: puerta; menos, expansor suave), con rango, ataque, retención, relajación, histéresis y pasa altos del detector. Su curva muestra el punto de lo que suena.
- **De-esser:** baja las eses por encima de una frecuencia (solo esa banda o toda la señal).
- **Saturación:** de cinta (simétrica) a válvula (armónicos pares), con tono, salida y mezcla en paralelo; sin aspereza (antialiasing).
- **Delay:** tiempo en ms o al tempo (figura de negra, corchea con puntillo, tresillos…), realimentación filtrada, ping-pong y mezcla (50 % deja el seco y el eco a tope; 100 %, solo el eco para un bus de envío).
- **Reverb:** sala, estudio, hall, catedral o «Envío de voz»: predelay, tamaño, duración (RT60), brillo, ancho, corte de graves y mezcla.
- **Tono:** sube o baja hasta una octava (y cents) sin cambiar la duración.
- **Limitador:** techo de pico verdadero (−1 dBTP de fábrica), ganancia de entrada y recuperación.

## Medidores

A la derecha del reproductor: barras L y R con la escala IEC (verde hasta −18 dB, amarillo hasta −6, rojo encima), la franja del RMS, la raya del pico retenido, el mayor pico desde el último clic, la **sonoridad** corta y la integrada («I»), y un aviso de **saturación** que queda encendido hasta que haces clic. Clic derecho: caída lenta, normal o rápida (12, 20 o 40 dB/s), borrar picos y sonoridad integrada, **Normalizar la mezcla** y quitar la ganancia maestra.

## Sonoridad (LUFS)

Rodaje mide con EBU R128, el estándar que usan las plataformas y la televisión.

- **Normalizar un clip:** Propiedades › Audio › fila «Sonoridad» › **Normalizar**: «−14 LUFS · redes y streaming», «−16 LUFS · podcast», «−23 LUFS · televisión (EBU R128)» u «Otro objetivo…». Mide el clip solo y ajusta su volumen (también sus keyframes, conservando la forma de la envolvente). Actúa sobre todos los clips de audio seleccionados o sobre el audio enlazado al video seleccionado.
- **Normalizar la mezcla:** clic derecho en los medidores › Normalizar la mezcla. Ajusta la ganancia maestra con lo medido desde el último clic, sin pasar de −1 dBTP.
- **Al exportar:** «Normalizar sonoridad» en Avanzado (−14, −16 o −23 LUFS). Mide el rango exportado entero («Midiendo la sonoridad»), calcula la ganancia sin que el pico verdadero pase de −1 dBTP y la aplica a la mezcla. Si harían falta más decibelios, se queda corta: no limita ni distorsiona. Los preajustes Web, Vertical, Cuadrado y Solo audio traen −14 LUFS.

## Ecualizador y compresor

- **Preajuste rápido:** Propiedades › Audio › fila «Voz y música», el menú de la cabecera de una pista (**Preajuste de audio**) o la pestaña **Dinámica**. Los de voz traen también ecualizador; los demás dejan el que haya.
  - **Voz para redes:** quita el retumbe, da presencia, compresión firme y no pasa de −1 dBTP.
  - **Podcast:** voz cálida con compresión suave y constante.
  - **Locución cálida:** cuerpo en los graves y compresión lenta en paralelo (85 %).
  - **Música: pegamento:** 1 o 2 dB de reducción lenta para unir una mezcla.
  - **Batería con pegada:** compresión en paralelo (45 %) que deja pasar el golpe.
  - **Limitador de mezcla:** ∞:1 con 5 ms de anticipación y techo de −1 dBTP.
  - **Multibanda voz:** tres bandas; frena el retumbe y las eses sin apagar la voz.
- **Ecualizador a mano:** Filtros › Añadir filtro › **Ecualizador** (corte de graves hasta 500 Hz; estantes de graves y agudos; campanas de medios y presencia con frecuencia y ancho Q; ±18 dB).

## Compresor (pestaña Dinámica)

Abre la pestaña **Dinámica** del inspector: muestra el compresor del clip de audio seleccionado; arriba eliges otro: cualquier **pista** o la **mezcla**. También se abre con clic derecho en la cabecera de una pista (**Compresor de la pista…**), en los medidores (**Compresor de la mezcla…**) o con **Abrir en Dinámica** en Filtros.

- **Curva y medidores:** la curva de transferencia (entrada contra salida, con la rodilla y la compensación), un punto con lo que suena y tres barras: **Entrada**, **Reducción** (compresor y limitador) y **Salida**, con el pico retenido.
- **A/B: sin compresor** compara con la señal sin comprimir y **Escuchar sidechain** deja oír lo que oye el detector. Solo cambian lo que suena en la vista previa (no el proyecto ni la exportación) y se apagan al salir de la pestaña.
- **Compresión:** umbral, relación (de 1:1 a ∞:1: 100 es limitador), ataque, relajación, rodilla, compensación y **mezcla** en paralelo. Umbral, relación y mezcla admiten **keyframes**: en un clip van con su material; en una pista o la mezcla, en el tiempo de la secuencia. **Relajación automática** suelta rápido tras un golpe y despacio tras una compresión larga; **Compensación automática** sube la mitad de lo que la curva quita a 0 dBFS.
- **Detector y sidechain:** pico o RMS (con su ventana), estéreo enlazado o por canal, **pasa altos del detector** (los graves no disparan la compresión), **anticipación** de 0 a 10 ms (el compresor ve el golpe antes de que suene; Rodaje compensa el retraso, todo sigue sincronizado) y **Sidechain**: otra pista dispara la compresión. Para que la música baje cuando habla la voz: compresor en la pista de música, sidechain «Pista Voz», umbral −30 dB, relación 10:1; con anticipación, la música ya bajó cuando empieza la palabra.
- **Multibanda:** tres bandas con dos cruces; cada una con su umbral (respecto del general) y su ganancia. Sin compresión suman plano.
- **Limitador:** limitador de pico verdadero al final, con techo (−1 dBTP de fábrica).

## Ducking: bajar la música bajo la voz

Selecciona el clip de **música** y pulsa **Bajar música bajo la voz** en **Herramientas › Audio › Bajar música bajo la voz…** (o en la barra de herramientas rápidas, o en el menú contextual del clip). El diálogo «Bajar música bajo la voz»:

- «Voz en «pista»»: la pista con la voz, entre las que suenan debajo de la música.
- Analiza la voz (con el módulo «Audio con IA» usa Silero; sin él, la energía del audio) y muestra «N tramos de voz; la música baja 12 dB en cada uno» con una barra de los tramos.
- «Cuánto baja la música» (−30 a −3 dB; 12 de fábrica), «Baja antes de la voz» (30–500 ms) y «Vuelve a subir en» (0,1–2 s).

**Aplicar** suma la envolvente a los keyframes de volumen de la música; lo que ya estaba animado se conserva. Un paso del historial.

## Voz en off

**Grabar voz** en **Herramientas › Texto y voz › Grabar voz en off…**, el botón de la barra rápida, el micrófono de la cabecera de una pista de audio o su menú contextual («Grabar voz en off aquí…»):

1. Elige el **micrófono**, la **pista** (nueva «Voz en off» o una existente) y si se silencia esa pista mientras grabas. Usa auriculares para que el micrófono no recoja lo que suena.
2. **Grabar**: cuenta atrás de 3 s y graba desde el cursor mientras suena el resto del proyecto. **Detener** o Esc termina.
3. Queda un WAV mono en la carpeta de medios generados, el medio «Voz en off <fecha>» en la biblioteca y el clip en la pista elegida (o en una nueva si estaba ocupada), seleccionado. Un paso del historial.

Sin micrófono: «Windows no tiene ningún micrófono activo. Conéctalo o actívalo en Configuración › Sistema › Sonido.»

Para una voz sintética, **Texto a voz** (ver [IA local](ia-local.md)).

## Quitar silencios

Selecciona el clip con sonido y pulsa **Quitar silencios** (**Herramientas › Audio › Quitar silencios…**, la barra rápida o el menú contextual del clip). Una barra roja marca lo que se corta y el estado dice «N tramos; el clip pasa de X s a Y s». Ajusta «Umbral bajo el nivel de la voz» (−60 a −10 dB; −35 de fábrica), «Silencio más corto que se quita» (0,1–3 s) y «Margen que se deja a cada lado» (0–500 ms). **Cerrar los huecos (ripple)** viene marcado si el ripple automático está activo. **Quitar** corta el clip y sus enlazados.

## Detectar ritmo

Selecciona la música y pulsa **Detectar ritmo** (**Herramientas › Audio › Detectar ritmo…**, la barra rápida o el menú contextual del clip). Muestra «Tempo: 120,0 BPM · N pulsos». «Poner un marcador en cada pulso» deja marcadores «Pulso» (para cortar a ritmo con el ajuste encendido); «Cortar el clip al ritmo» corta cada pulso, cada 2, cada 4 (compás) o cada 8.

**Montaje al ritmo.** Si además de la música seleccionas los clips del montaje (las tomas o las fotos de la pista principal, con Ctrl+clic), aparece **«Ajustar al ritmo los otros N clips seleccionados»**: cada clip, de izquierda a derecha, pasa a terminar en el pulso que le toca (cada pulso, cada 2, cada 4…) y lo que sigue en su pista se corre para quedar pegado, así todos los cortes caen en la música. Un clip no crece más allá de su material (si le faltan cuadros, se queda hasta donde llega), los títulos y las fotos no tienen tope, y los clips con reversa, curva o congelado solo se acortan. Los enlazados van juntos y todo se deshace en un paso con Ctrl+Z.

## Reducir ruido

Necesita el módulo «Audio con IA». Selecciona el clip con voz: puedes abrirlo de inmediato desde el menú contextual del clip (**Reducir ruido…**), desde **Herramientas › Audio › Reducir ruido…** o desde la barra rápida. El diálogo limpia la voz y deja escuchar «A: original» y «B: limpio»; «Cuánto ruido se quita» va de 6 a 48 dB (todo). Con menos intensidad queda algo del ambiente y la voz suena más natural. Al aplicar, el clip pasa a un WAV nuevo «<nombre> (sin ruido)»; el original no se toca. Límite: una hora por clip.

## Velocidad y tono

Al cambiar la velocidad de un clip con sonido, **Mantener tono** (pestaña Velocidad) conserva el tono de la voz. Por encima de 8× se oye granuloso y por debajo de 0,25× metálico. La reversa sola no cambia el tono.
