# Audio

Volumen, sonoridad, ecualizador, compresor, ducking, voz en off y las herramientas que limpian o cortan el sonido. El video sigue siempre al reloj del audio: si el equipo no llega, se pierde un cuadro, nunca una muestra.

## Volumen

- **Por clip:** Propiedades › sección **Audio**: «Volumen» de −60 (silencio) a +24 dB, con keyframes; «Fundidos»: «Entrada» y «Salida» en segundos (hasta la mitad del clip). El botón de la cabecera restablece volumen y fundidos.
- **Por pista:** en la cabecera de la pista de audio, arrastra el texto «Volumen 0,0 dB» a los lados (0,25 dB por píxel; con Mayús, 0,05); doble clic vuelve a 0 dB. Clic derecho en la cabecera: **Ecualizador y compresor** de la pista (Sin ecualizador ni compresor, Voz clara, Podcast, Música suave) y **Grabar voz en off aquí…**.
- **Envolvente:** con la herramienta de envolvente (**E**) sobre un clip de audio, un clic pone un punto y lo arrastra, agarrar un punto lo mueve sin pasar a sus vecinos y clic derecho lo quita. La altura es lineal en amplitud: 0 dB a media altura, +6 dB arriba y silencio abajo. El interruptor **bloquear envolventes** de la barra evita tocarlas sin querer.
- Silenciar una pista: el altavoz de su cabecera.

## Medidores

A la derecha del reproductor: barras L y R con la escala IEC (verde hasta −18 dB, amarillo hasta −6, rojo encima), la franja del RMS, la raya del pico retenido, el mayor pico desde el último clic, la **sonoridad** corta y la integrada («I»), y un aviso de **saturación** que queda encendido hasta que haces clic. Clic derecho: caída lenta, normal o rápida (12, 20 o 40 dB/s), borrar picos y sonoridad integrada, **Normalizar la mezcla** y quitar la ganancia maestra.

## Sonoridad (LUFS)

Rodaje mide con EBU R128, el estándar que usan las plataformas y la televisión.

- **Normalizar un clip:** Propiedades › Audio › fila «Sonoridad» › **Normalizar**: «−14 LUFS · redes y streaming», «−16 LUFS · podcast», «−23 LUFS · televisión (EBU R128)» u «Otro objetivo…». Mide el clip solo y ajusta su volumen (también sus keyframes, conservando la forma de la envolvente). Actúa sobre todos los clips de audio seleccionados o sobre el audio enlazado al video seleccionado.
- **Normalizar la mezcla:** clic derecho en los medidores › Normalizar la mezcla. Ajusta la ganancia maestra con lo medido desde el último clic, sin pasar de −1 dBTP.
- **Al exportar:** «Normalizar sonoridad» en Avanzado (−14, −16 o −23 LUFS). Mide el rango exportado entero («Midiendo la sonoridad»), calcula la ganancia sin que el pico verdadero pase de −1 dBTP y la aplica a la mezcla. Si harían falta más decibelios, se queda corta: no limita ni distorsiona. Los preajustes Web, Vertical, Cuadrado y Solo audio traen −14 LUFS.

## Ecualizador y compresor

- **Preajuste rápido:** Propiedades › Audio › fila «Voz y música»: **Voz clara** (quita el retumbe, da presencia y empareja la voz), **Podcast** (voz cálida con compresión firme) o **Música suave** (más cuerpo y aire, compresión lenta). Pone un ecualizador y un compresor en un paso; «Sin ecualizador ni compresor» los quita.
- **A mano:** Filtros › Añadir filtro › **Ecualizador** (corte de graves hasta 500 Hz; estantes de graves y agudos; campanas de medios y presencia con frecuencia y ancho Q; ±18 dB) y **Compresor** (umbral, relación 1:1 a 20:1, ataque, relajación, compensación y rodilla). Solo se aplica el primero de cada tipo que esté activo. También en las pistas.

## Ducking: bajar la música bajo la voz

Selecciona el clip de **música** y pulsa **Bajar música bajo la voz** en Herramientas (Editar › Ducking automático..., o el menú del clip). El diálogo «Ducking automático»:

- «Voz en «pista»»: la pista con la voz, entre las que suenan debajo de la música.
- Analiza la voz (con el módulo «Audio con IA» usa Silero; sin él, la energía del audio) y muestra «N tramos de voz; la música baja 12 dB en cada uno» con una barra de los tramos.
- «Cuánto baja la música» (−30 a −3 dB; 12 de fábrica), «Baja antes de la voz» (30–500 ms) y «Vuelve a subir en» (0,1–2 s).

**Aplicar** suma la envolvente a los keyframes de volumen de la música; lo que ya estaba animado se conserva. Un paso del historial.

## Voz en off

**Grabar voz** en Herramientas, el micrófono de la cabecera de una pista de audio o su menú («Grabar voz en off aquí…»):

1. Elige el **micrófono**, la **pista** (nueva «Voz en off» o una existente) y si se silencia esa pista mientras grabas. Usa auriculares para que el micrófono no recoja lo que suena.
2. **Grabar**: cuenta atrás de 3 s y graba desde el cursor mientras suena el resto del proyecto. **Detener** o Esc termina.
3. Queda un WAV mono en la carpeta de medios generados, el medio «Voz en off <fecha>» en la biblioteca y el clip en la pista elegida (o en una nueva si estaba ocupada), seleccionado. Un paso del historial.

Sin micrófono: «Windows no tiene ningún micrófono activo. Conéctalo o actívalo en Configuración › Sistema › Sonido.»

Para una voz sintética, **Texto a voz** (ver [IA local](ia-local.md)).

## Quitar silencios

Selecciona el clip con sonido y pulsa **Quitar silencios** (Herramientas, Editar o el menú del clip). Una barra roja marca lo que se corta y el estado dice «N tramos; el clip pasa de X s a Y s». Ajusta «Umbral bajo el nivel de la voz» (−60 a −10 dB; −35 de fábrica), «Silencio más corto que se quita» (0,1–3 s) y «Margen que se deja a cada lado» (0–500 ms). **Cerrar los huecos (ripple)** viene marcado si el ripple automático está activo. **Quitar** corta el clip y sus enlazados.

## Detectar ritmo

Selecciona la música y pulsa **Detectar ritmo**. Muestra «Tempo: 120,0 BPM · N pulsos». «Poner un marcador en cada pulso» deja marcadores «Pulso» (para cortar a ritmo con el ajuste encendido); «Cortar el clip al ritmo» corta cada pulso, cada 2, cada 4 (compás) o cada 8.

## Reducir ruido

Necesita el módulo «Audio con IA». Selecciona el clip con voz; el diálogo limpia la voz y deja escuchar «A: original» y «B: limpio»; «Cuánto ruido se quita» va de 6 a 48 dB (todo). Con menos intensidad queda algo del ambiente y la voz suena más natural. Al aplicar, el clip pasa a un WAV nuevo «<nombre> (sin ruido)»; el original no se toca. Límite: una hora por clip.

## Velocidad y tono

Al cambiar la velocidad de un clip con sonido, **Mantener tono** (pestaña Velocidad) conserva el tono de la voz. Por encima de 8× se oye granuloso y por debajo de 0,25× metálico. La reversa sola no cambia el tono.
