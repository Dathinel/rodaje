# Dominio propio para la página de Rodaje

Estas instrucciones se dejan preparadas para quien administre el dominio. **No se ha ejecutado ningún cambio de DNS ni de GitHub Pages.** Antes de aplicarlas, confirmar con Daniel el dominio definitivo y que la publicación de Pages apunte a `docs-web/`.

## Configuración prevista

1. En GitHub, abrir **Settings > Pages** del repositorio `Dathinel/rodaje` y seleccionar el origen que publique la carpeta `docs-web/`. Esperar a que la URL `https://dathinel.github.io/rodaje/` cargue esta página antes de tocar DNS.
2. En **Settings > Pages > Custom domain**, escribir el dominio elegido, por ejemplo `rodaje.example.com`. GitHub creará o actualizará el archivo `CNAME` en el origen publicado; revisar el cambio antes de aceptarlo.
3. En el proveedor DNS, crear un registro `CNAME` del subdominio elegido hacia `dathinel.github.io`. Ejemplo: nombre `rodaje`, destino `dathinel.github.io`, sin proxy ni redirección del proveedor.
4. Esperar la propagación y comprobar que GitHub marca el dominio como verificado. Activar **Enforce HTTPS** solo después de que el certificado esté emitido.
5. Comprobar en una ventana privada que `https://<dominio>/` abre la página, que los enlaces a Releases y al catálogo funcionan y que no hay contenido mixto.

## Si se usa el dominio raíz

GitHub Pages requiere registros `A` y, si se desea IPv6, `AAAA` para el dominio raíz. Consultar los valores vigentes en la documentación oficial de GitHub Pages en el momento de configurarlo; no fijar aquí direcciones que puedan cambiar. Añadir además `www` como `CNAME` a `dathinel.github.io` o redirigirlo al dominio canónico elegido.

## Reversión

Para deshacer la configuración, retirar primero el dominio personalizado en GitHub Pages, esperar la confirmación y después borrar los registros DNS creados para él. Conservar la URL `github.io` como acceso de recuperación hasta verificar que no quedan enlaces públicos al dominio retirado.
