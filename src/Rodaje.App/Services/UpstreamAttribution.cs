namespace Rodaje.App.Services;

/// <summary>
/// Crédito upstream que exige el término adicional 1 de <c>LICENSE</c> (GPLv3, Sección 7 b y c): debe verse en la
/// interfaz con un enlace funcional al repositorio oficial, y el término 3 prohíbe quitarlo o alterarlo.
/// </summary>
internal static class UpstreamAttribution
{
    public const string RepositoryUrl = "https://github.com/Dathinel/rodaje";

    public const string ShortCredit = "Código abierto upstream: Rodaje";

    public const string Notice = "Basado en el editor de video de código abierto de Daniel (Dathinel) / Rodaje (" + RepositoryUrl + ")";

    public const string Copyright = "Copyright (C) 2026 Daniel (Dathinel)";

    /// <summary>Los «Appropriate Legal Notices» de la GPLv3 (secciones 0 y 5.d): sin garantía, se puede redistribuir y dónde leer la licencia.</summary>
    public const string LicenseLine = "Rodaje es software libre: puedes redistribuirlo y modificarlo bajo la GNU GPLv3 con los términos adicionales del archivo LICENSE.txt, junto al programa. Se ofrece SIN NINGUNA GARANTÍA. El nombre y los logotipos de Rodaje no se incluyen en la licencia (TRADEMARK.md).";

    public const string FfmpegCredit = "Usa bibliotecas de FFmpeg (https://ffmpeg.org) bajo la LGPL v3; avisos en THIRD-PARTY-NOTICES.txt.";

    public static Uri RepositoryUri { get; } = new(RepositoryUrl, UriKind.Absolute);
}
