# Editor automático para video vertical y una interfaz más clara para quien llega nuevo (idea para la 1.0.5)

**Origen:** Daniel, 20-nov-2026. Es una **idea guardada**, no un plan aprobado: antes de escribir código hace falta el plan por fases con el stack comparado (regla 1 de `CLAUDE.md` de la raíz). Aquí quedan lo que pidió, lo que ya existe en Rodaje, qué se puede hacer con IA local y una recomendación.

## Lo que pidió (con sus palabras ordenadas)

1. Un **editor automático genérico** para las categorías principales de TikTok, Instagram y cualquier sistema de video **vertical**, **aparte de las opciones normales** del editor, parecido a CapCut.
2. **Predeterminados** del tipo «vlog» y «contar una historia», con **niveles de edición**, de baja a alta.
3. Que estudie la **retención de la audiencia**, los **sonidos psicológicos**, los patrones, los ganchos y el marketing.
4. Posibilidad de **tendencias actuales por región** y **categorías** (humor, informativo, política…).
5. Preguntó si se puede con **IA local** o si es demasiado ambicioso. Es contenido genérico, **para quien lo quiera**; las categorías, **como complementos aparte**, porque todo es local y pesa (ritmo de los cortes, imágenes que coloca, sonido satisfactorio, animaciones).
6. Una **pequeña actualización de la interfaz**: le gusta como está, pero a un usuario nuevo le puede abrumar. Quizá un **tutorial más detallado**, o reorganizar para que se entienda mejor en conjunto.

## Lo que Rodaje ya tiene y se aprovecha

| Pieza | Estado | Para qué sirve aquí |
|---|---|---|
| Subtítulos automáticos (módulo), con palabras y hora | Hecho | Transcribir el medio entero; base del corte por contenido |
| Subtítulos palabra por palabra, karaoke y 4 estilos animados de fábrica (1.0.4) | Hecho | Subtítulos dinámicos «de TikTok» |
| Textos animados: 10 entradas y salidas y 4 movimientos en bucle (1.0.4) | Hecho | Títulos y ganchos con movimiento |
| Quitar silencios (Herramientas) | Hecho | Nivel bajo |
| Detectar ritmo y Montaje al ritmo | Hecho | Cortes al compás de la música |
| Plantillas de movimiento (D-064) | Hecho | Rótulos y estructuras listas; empaquetables en complementos |
| Reencuadre y seguimiento de personas (módulo de visión; app Rastrear) | Hecho | Pasar de 16:9 a 9:16 sin cortar al sujeto |
| Exportación vertical 9:16 y varias versiones a la vez | Hecho | Salida final |
| Complementos y catálogo firmado | Hecho | Instalar categorías, sonidos y modelos por separado |
| Módulos de IA locales (voz, traducción, audio, visión) | Hecho | Aprovechar el mismo mecanismo para los modelos nuevos |
| Render en caché y proxies | Hecho | Que un montaje automático largo siga fluido |

## Niveles de edición

1. **Nivel 1, limpiar** (sin creatividad, 100 % determinista): quitar silencios y muletillas, subtítulos con estilo, reencuadre 9:16 con seguimiento y normalizar el volumen.
2. **Nivel 2, dar ritmo:** cortes a la cadencia del predeterminado (por ejemplo, un cambio de plano o un zoom cada 2 a 4 s), zooms de énfasis en las palabras clave, efectos de sonido en los cambios, música de fondo con reducción al hablar, rótulos de las plantillas.
3. **Nivel 3, estructurar:** gancho en los primeros segundos, desarrollo y cierre con llamada a la acción; elegir los mejores fragmentos de un video largo; sugerir imágenes de apoyo de la biblioteca del usuario; títulos de portada.

## Predeterminados («recetas»)

Cada receta es un archivo de datos editable (cadencia de cortes, tipo de gancho, estilo de subtítulos, sonidos, plantillas, duración objetivo), no código: **vlog**, **contar una historia**, **tutorial o explicación**, **reseña**, **humor y sketch**, **informativo**, **entrevista o podcast recortado**, **antes y después**. Las recetas se pueden duplicar y modificar.

## ¿Se puede con IA local, o es demasiado ambicioso?

Respuesta corta: **la mayor parte sí; lo ambicioso se deja fuera o va aparte.** Equipo de referencia: portátil con RTX 3050 de 4 GB de memoria de video.

| Capacidad | Viabilidad local | Cómo |
|---|---|---|
| Transcribir, quitar silencios y muletillas, subtítulos animados | **Sí, ya existe** | Módulos actuales |
| Reencuadre vertical con seguimiento | **Sí, ya existe** | Módulo de visión |
| Cortar al ritmo, zooms de énfasis, efectos en los cambios | **Sí, reglas** | Detección de picos de energía y de palabras clave con el audio y el texto; sin modelo grande |
| Escoger los mejores fragmentos y proponer ganchos o títulos | **Sí, con límites** | Un modelo de lenguaje pequeño y cuantizado (de 3 a 8 mil millones de parámetros) entra en 4 GB con cuantización fuerte; mejor en CPU o con memoria compartida; calidad modesta pero útil como sugerencia editable |
| Entender el contenido visual para elegir imágenes | **Parcial** | Modelo pequeño de visión; lento en 4 GB, mejor como proceso por lotes |
| Predecir retención real de la audiencia | **No** | No se puede validar sin las analíticas de la propia plataforma; se ofrecen reglas conocidas, no promesas |
| Tendencias del momento por región | **No de forma local pura** | Exige datos de internet (ver abajo) |
| Generar B-roll o música con IA generativa | **Aparte y pesado** | Fuera de la primera versión |

### Tendencias por región

Lo local no puede saber qué es tendencia hoy. Opciones, de mejor a peor: (1) **paquetes de tendencias firmados**, curados por una persona y descargados por el catálogo (estructuras, ritmos y estilos de moda, sin copiar contenido ajeno); (2) el usuario las **importa a mano** (una plantilla de un video que le gustó); (3) raspar las plataformas: **no**, rompe sus condiciones y es frágil. Recomendado: (1) y (2).

### Retención, ganchos y «sonidos psicológicos»

Son técnicas de edición conocidas: un gancho en los primeros 1 a 3 s, cambios de plano frecuentes, subtítulos grandes, un sonido en cada cambio, cierre con una pregunta o un bucle. Se codifican como **reglas legibles dentro de cada receta**, con su explicación, para que el usuario aprenda y las ajuste. **No se prometen resultados ni se usa manipulación**: en la categoría de política solo plantillas neutrales de formato (informativo, explicación), nada de optimizar persuasión.

## Cómo encaja en el producto

- **Aparte de las opciones normales:** un modo o espacio de trabajo «Auto», con su propia entrada en el menú y su asistente (elige receta, nivel, duración y voz o música; muestra una **vista previa del plan** antes de aplicar; el resultado es una línea de tiempo normal y editable, con todo en un paso del historial).
- **Categorías como complementos:** cada paquete trae recetas, plantillas, estilos y sonidos; se instala desde el gestor, firmado, y se actualiza por separado. Pesan por los sonidos y los modelos.
- **Modelos de IA como módulos:** el mismo mecanismo de procesos aparte que ya usan voz, traducción y visión; la base y la distribución completa no los incluyen.

## Legal

- **Sonidos y música:** solo propios o con licencia libre (CC0 o equivalente) y anotados en `LICENCIAS.md` de cada paquete. **Nunca copiar el audio de las tendencias de una plataforma** (derechos).
- Fuentes, imágenes y modelos: licencia permisiva y comprobada, como en el resto de Rodaje.
- Decir claramente que el resultado es una **propuesta automática**.

## Interfaz: ayuda para quien llega nuevo

Lo que ya hay: un **recorrido de 6 pasos** (biblioteca, visor, Herramientas, línea de tiempo, inspector y exportar), **pistas contextuales** de una sola vez, **espacios de trabajo** (Edición, Color y Audio) y un preajuste de exportación vertical. Daniel: le gusta como está y no se quita nada a quien ya sabe usarlo. Ideas, de menor a mayor esfuerzo:

1. **Tutorial más detallado y por tareas**, además del recorrido: «hacer tu primer video en 5 minutos» con un **proyecto de ejemplo guiado** (un clip incluido, subtítulos, un corte, un título animado, exportar); paso a paso con resaltado, que se puede pausar.
2. **Un modo sencillo** que oculta paneles poco usados al principio y los revela con el uso («Mostrar más»), sin cambiar nada para el modo avanzado.
3. **Un panel «qué hago ahora»** que propone el siguiente paso según el estado (sin medios, con medios, con montaje, listo para exportar).
4. Una **pasada de nombres y agrupación** de Herramientas y del inspector, probada con **usuarios nuevos reales** (el que ya usa el editor y dio la idea del reporte puede ser el primero).

## Recomendación

1. **1.0.5:** el **tutorial por tareas y el proyecto de ejemplo** (puntos 1 y 3 de la interfaz) y el **editor automático de niveles 1 y 2** con 3 o 4 recetas (vlog, historia, tutorial, informativo), **solo con reglas y los módulos actuales**, sin modelo de lenguaje nuevo.
2. **1.1:** el **nivel 3** con un modelo de lenguaje local pequeño como módulo, el **modo sencillo** y los **paquetes de categorías y de tendencias** como complementos.
3. Lo generativo (B-roll y música con IA) y la retención «predicha»: fuera por ahora.

## Preguntas abiertas (se avanza con la recomendada, sin esperar)

1. ¿El modo «Auto» es un espacio de trabajo propio o un asistente que parte de Herramientas? **Recomendado:** asistente en Herramientas, más un espacio «Auto» cuando haya varias recetas.
2. ¿Qué mínimo de hardware se anuncia para el nivel 3? **Recomendado:** 8 GB de RAM y funcionar en CPU; mejora con GPU.
3. ¿Quién cura los paquetes de tendencias y cada cuánto? **Recomendado:** Daniel o un colaborador, una vez al mes, firmados con la clave del catálogo.
4. ¿Qué sonidos se incluyen y de dónde? **Recomendado:** una biblioteca propia pequeña (CC0) en el primer paquete y no más.

## Dependencias y riesgos

- Depende de la 1.0.4 (subtítulos animados y textos en bucle) y del repaso con ratón de B9.
- Riesgo: contenido «genérico» que se parezca demasiado entre usuarios; las recetas editables y los paquetes de la comunidad lo mitigan.
- Riesgo: peso de los complementos (sonidos y modelos) y rendimiento en 4 GB de video; todo opcional y por separado.
- Riesgo: una interfaz nueva que complique lo que ya gusta; por eso el modo sencillo es opcional y la ayuda no quita nada.
