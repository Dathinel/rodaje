<!-- Gracias por contribuir a Rodaje. Sigue las pautas de CONTRIBUTING.md. -->
<!-- Un PR por tema. Los cambios de arquitectura o alcance grande se discuten previamente en un issue. -->

## Qué cambia

<!-- Describe en una o dos frases qué introduce este PR. Si resuelve o cierra un issue existente, enlázalo: «Cierra #123» o «Relacionado con #123». -->

## Por qué

<!-- Explica la motivación técnica, el defecto que soluciona o la mejora que aporta. Si se apoya en una decisión de docs/decisiones.md o el plan de interfaz, menciónalo aquí. -->

## Cómo se probó

<!-- Detalla las pruebas automáticas añadidas o modificadas y su cobertura. Si incluye pruebas manuales, indica entorno (versión de Windows, GPU/D3D11) y medios de prueba utilizados. -->

- [ ] `Probar.bat` pasa en local (o `build\dotnet-local.cmd test`).
- [ ] Si tocó `src\Rodaje.App`: `Rodaje.bat` compila y publica con Native AOT con 0 advertencias, y la app arranca correctamente.
- [ ] Las pruebas nuevas no usan `Skip` salvo justificación por falta de hardware (con `[Trait("Categoria", "...")]`).

## Lista de comprobación

- [ ] **Firma DCO:** Todos los commits llevan `Signed-off-by: Nombre <correo>` (`git commit -s`), coincidente con la autoría.
- [ ] **Licencia y procedencia:** El código es original o bajo licencia permisiva compatible (MIT/Apache-2.0/BSD); sin código GPL externo ni activos comerciales.
- [ ] **Convenciones de estilo:** Identificadores en inglés; comentarios, textos de interfaz, notas y commits en español.
- [ ] **Zonas protegidas:** No modifica archivos protegidos (`build/`, `.github/`, `LICENSE`, `CLAUDE.md`, `TRADEMARK.md`, dependencias en `packages.lock.json` o `UpstreamAttribution.cs`) salvo autorización expresa en el issue.
- [ ] **Documentación:** Si altera interfaz o comportamiento de usuario, se actualizó la guía en `docs/guia/` o `docs/arquitectura/`.
- [ ] **Compatibilidad de proyecto:** Si altera el modelo `.rodaje`, se incrementó `schemaVersion` y se añadió migración con prueba hacia atrás.
