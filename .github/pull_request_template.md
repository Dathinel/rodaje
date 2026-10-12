<!-- Gracias por contribuir. Cómo se trabaja: CONTRIBUTING.md. Un PR por tema; los cambios grandes se hablan antes en un issue. -->

## Qué cambia

<!-- Una o dos frases. Enlaza el issue si lo hay: «Cierra #123». -->

## Por qué

<!-- El problema que resuelve o la mejora que trae. Si toca una decisión de docs/decisiones.md, dilo. -->

## Cómo se probó

<!-- Pruebas nuevas o cambiadas y qué cubren. Si lo probaste a mano, en qué equipo (Windows, GPU) y con qué archivos. -->

- [ ] `Probar.bat` pasa en local.
- [ ] Si toqué `src\Rodaje.App`: `Rodaje.bat` publica con Native AOT sin advertencias y la app abre.

## Lista de comprobación

- [ ] Todos los commits llevan `Signed-off-by` (`git commit -s`), con mi nombre y correo reales.
- [ ] El código es mío o de una licencia permisiva; no viene de proyectos GPL ni de programas comerciales.
- [ ] Identificadores en inglés; comentarios, mensajes y commits en español.
- [ ] Toda lógica nueva o corregida tiene pruebas.
- [ ] Si añadí una dependencia: `packages.lock.json`, `LICENCIAS.md` y, si se redistribuye, `THIRD-PARTY-NOTICES.txt` están actualizados.
- [ ] Si cambia algo que ve el usuario: actualicé `docs/arquitectura/` y la guía (`docs/guia/`).
- [ ] Si cambia el formato `.rodaje`: subí `schemaVersion`, escribí la migración y su prueba.
