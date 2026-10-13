namespace Rodaje.App.Services.Crashes;

/// <summary>
/// El aviso en la ventana de un error que nadie esperaba: un texto fijo en español con la ruta del informe. El mensaje de
/// la excepción (en inglés y técnico) va solo al informe.
/// </summary>
public static class UnexpectedErrorNotice
{
    public const string Title = "Algo falló";

    /// <summary>Dónde quedan los informes, para cuando no se pudo escribir uno o no se sabe su ruta.</summary>
    public const string ReportsFolder = @"%LOCALAPPDATA%\Rodaje\fallos";

    public static string Describe(string? reportPath) =>
        "Rodaje sigue abierto con tu trabajo. Si algo no se ve bien, deshaz el último paso con Ctrl+Z, o guarda y vuelve a "
        + "abrir el proyecto. El informe del error quedó en "
        + (string.IsNullOrWhiteSpace(reportPath) ? ReportsFolder : reportPath) + ".";
}
