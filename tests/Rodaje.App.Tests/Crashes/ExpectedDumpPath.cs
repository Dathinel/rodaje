namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// La ruta del minivolcado tal como debe salir en el informe, calculada sin el limpiador (antes las pruebas comparaban con
/// el propio limpiador y no podían fallar): las pruebas lo dejan en la carpeta temporal, que en Windows pasa a
/// <c>%TEMP%</c> con el resto de la ruta intacto; fuera de Windows no hay carpetas del usuario que tapar.
/// </summary>
internal static class ExpectedDumpPath
{
    public static string Of(string dump)
    {
        if (!OperatingSystem.IsWindows())
        {
            return dump;
        }

        string? temp = new[] { Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP") }
            .Select(t => (t ?? string.Empty).Trim().TrimEnd('\\', '/'))
            .Where(t => t.Length >= 3 && dump.StartsWith(t + @"\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Length)
            .FirstOrDefault();
        Assert.NotNull(temp);
        return "%TEMP%" + dump[temp.Length..];
    }
}
