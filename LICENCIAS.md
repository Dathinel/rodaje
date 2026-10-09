# Licencias de las dependencias

Este archivo lista cada componente de terceros que FotoToma usa o planea usar, con su licencia verificada, la fuente donde se verificó y lo que esa licencia implica para un programa MIT que se entrega como ejecutable autocontenido. Se actualiza cada vez que se añade, quita o actualiza una dependencia. La última verificación se hizo el 9 de octubre de 2026 leyendo los metadatos de los paquetes en nuget.org y los archivos de licencia de cada repositorio.

Hay una diferencia importante entre dos tipos de dependencias. Las que se usan solo para compilar o probar, como xUnit, nunca salen del equipo de desarrollo: no se copian a `dist\` ni viajan con el programa, así que su licencia solo importa para el uso interno. Las que se redistribuyen dentro de `dist\`, como el runtime de .NET o en el futuro las DLL de FFmpeg, imponen obligaciones al entregar el programa, y sus avisos deben ir en `THIRD-PARTY-NOTICES.txt`.

## Dependencias actuales (fase 1)

| Componente | Versión | Licencia | ¿Se redistribuye? | Fuente | Qué implica |
|---|---|---|---|---|---|
| SDK y runtime de .NET | 10.0.401 | MIT, © .NET Foundation and Contributors | Sí: el runtime queda embebido en el ejecutable autocontenido o AOT | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT | Hay que incluir el aviso de copyright y el texto MIT junto al ejecutable. El runtime contiene a su vez código de terceros listado en su propio archivo de avisos (https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), que también se incluye. |
| xunit | 2.9.3 | Apache-2.0, © .NET Foundation | No, solo pruebas | https://www.nuget.org/packages/xunit/2.9.3 | Uso libre en desarrollo. Si algún día se redistribuyera, exigiría conservar el aviso y el archivo NOTICE. |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0, © .NET Foundation | No, solo pruebas | https://www.nuget.org/packages/xunit.runner.visualstudio/4.0.1 | Igual que xunit. Se declara con `PrivateAssets=all`, así que no se propaga. |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT, © Microsoft Corporation | No, solo pruebas | https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.10.1 | Ninguna obligación práctica fuera del desarrollo. |
| coverlet.collector | 10.1.0 | MIT, © 2018 Toni Solarin-Sodara | No, solo pruebas | https://www.nuget.org/packages/coverlet.collector/10.1.0 | Ninguna obligación práctica fuera del desarrollo. |
| CsCheck | 4.9.1 | Apache-2.0, © Anthony Lloyd | No, solo pruebas | https://www.nuget.org/packages/CsCheck/4.9.1 | Uso libre en desarrollo para pruebas basadas en propiedades. |

## Dependencias previstas

Estas dependencias todavía no están en el proyecto. Se listan para que la decisión de usarlas se tome con la licencia ya revisada. Las versiones indicadas son las estables más recientes en nuget.org en la fecha de verificación y se fijarán al incorporarlas.

| Componente | Versión revisada | Licencia | ¿Se redistribuye? | Fuente | Qué implica |
|---|---|---|---|---|---|
| FFmpeg, compilación `lgpl-shared` de BtbN | por fijar | LGPL versión 3 (las compilaciones LGPL de BtbN usan `--enable-version3` y `COPYING.LGPLv3`) | Sí, como DLL separadas | https://github.com/BtbN/FFmpeg-Builds/blob/master/variants/defaults-lgpl.sh y https://ffmpeg.org/legal.html | Enlace dinámico obligatorio, DLL sin renombrar, código fuente exacto u oferta escrita, línea de configuración, crédito en "Acerca de", sin prohibir la ingeniería inversa de esa parte. Detalle en `docs/legal.md`. Los scripts de compilación de BtbN son MIT, pero eso no cambia la licencia de los binarios. |
| FFmpeg.AutoGen | 9.0.1.1 | MIT, © 2025 Ruslan Balanukhin (Rationale One) | Sí | https://www.nuget.org/packages/FFmpeg.AutoGen/9.0.1.1/License | Solo exige incluir el aviso. Las versiones antiguas del paquete eran LGPL; el cambio a MIT se produjo en la serie 8.x, así que nunca debe fijarse una versión anterior a esa serie sin revisar su licencia. La licencia MIT cubre solo las vinculaciones de C#, no las DLL de FFmpeg. |
| Vortice.Windows (Direct3D11, DXGI y afines) | 3.8.3 | MIT, © Amer Koleci and Contributors | Sí | https://www.nuget.org/packages/Vortice.Direct3D11/3.8.3 | Solo exige incluir el aviso. |
| Windows App SDK (WinUI 3) | 2.5.1 | Términos de licencia de software de Microsoft (no MIT) | Sí | https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1/License | El código fuente de WinUI en GitHub es MIT (https://github.com/microsoft/microsoft-ui-xaml), pero los binarios que entrega el paquete NuGet se rigen por términos propios de Microsoft. Esos términos permiten redistribuir los archivos que el paquete coloca junto a la aplicación, también en despliegues autocontenidos, a condición de que la aplicación añada funcionalidad propia significativa, que distribuidores y usuarios acepten términos que protejan esos archivos y a Microsoft, y que no se sometan a una licencia que obligue a publicar su código. La licencia MIT de FotoToma cubre su código; los binarios de Microsoft conservan sus propios términos, y eso debe indicarse en los avisos. |
| NAudio | 3.1.0 | MIT, © Mark Heath | Sí | https://www.nuget.org/packages/NAudio/3.1.0 | Solo exige incluir el aviso. |
| Whisper.net | 1.9.1 | MIT, © 2024 sandrohanea | Sí | https://www.nuget.org/packages/Whisper.net/1.9.1/License | Solo exige incluir el aviso. El paquete de runtime nativo (Whisper.net.Runtime 1.9.1) también es MIT. |
| whisper.cpp | la que traiga Whisper.net | MIT, © The ggml authors | Sí, dentro del runtime de Whisper.net | https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE | Solo exige incluir el aviso. |
| Pesos de los modelos Whisper | por fijar | MIT, © 2022 OpenAI (cubre código y pesos) | Sí, como archivo de modelo | https://github.com/openai/whisper/blob/main/LICENSE | Se registra la URL y el hash del archivo exacto descargado. Los pesos convertidos al formato de whisper.cpp son una conversión de los originales; antes de incluirlos se confirma en su página de descarga que mantienen la licencia MIT. |
| ONNX Runtime (Microsoft.ML.OnnxRuntime) | 1.31.0 | MIT, © Microsoft Corporation | Sí | https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.31.0 y https://github.com/microsoft/onnxruntime/blob/main/LICENSE | Exige incluir el aviso. ONNX Runtime trae su propio archivo de avisos de terceros, que también debe viajar con el programa. |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | MIT, © Microsoft Corporation | Sí | https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML/1.24.4/License | El paquete es MIT, pero depende de Microsoft.AI.DirectML (1.15.4), que se rige por los términos de licencia de software de Microsoft para DirectML (https://www.nuget.org/packages/Microsoft.AI.DirectML/1.15.4/License). Esos términos permiten distribuir DirectML dentro de aplicaciones que usan aprendizaje automático en Windows, pero no como producto independiente, y prohíben quitar los avisos de Microsoft. |
| Modelo de quitar fondo (BiRefNet, MODNet o U²-Net) | por elegir | MIT (BiRefNet) o Apache-2.0 (MODNet, U²-Net) según el repositorio | Sí, como archivo de modelo | Ver `docs/legal.md` | Antes de incluirlo se verifica la licencia del archivo de pesos concreto y de sus datos de entrenamiento. RMBG-1.4 y RMBG-2.0 quedan excluidos por ser de uso no comercial. |

## Herramientas de desarrollo que nunca se redistribuyen

| Herramienta | Licencia | Nota |
|---|---|---|
| FFmpeg 9.0 de gyan.dev (instalado con winget) | GPL | Solo para pruebas y preparación de material en el equipo de desarrollo. Nunca se copia a `dist\`. |
| gitleaks | MIT (https://github.com/gitleaks/gitleaks) | Escaneo de secretos del historial antes de publicar. |
| Git y GitHub CLI | GPL-2.0 (Git) y MIT (gh) | Herramientas globales de gestión, no forman parte del programa. |
