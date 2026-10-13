using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// La ruta del minivolcado tal como debe salir en el informe, calculada sin el limpiador (antes las pruebas comparaban con
/// el propio limpiador y no podían fallar). Las pruebas lo dejan en la carpeta temporal: en Windows, la carpeta del
/// usuario más larga que la contiene (<c>TEMP</c> puede venir con el nombre corto 8.3 y la ruta con el largo) pasa a su
/// variable y el resto, que cuelga de <c>AppData</c>, queda intacto; fuera de Windows no hay carpetas del usuario. Dentro
/// de <c>%TEMP%</c> solo quedan a la vista las carpetas <c>rodaje-…</c>: las demás salen como <c>%TEMP%\…\archivo</c>.
/// <c>%TEMP%</c> es <c>TEMP</c>, <c>TMP</c> y <c>Path.GetTempPath()</c>, cada una en su forma 8.3 y en su nombre largo
/// (#263): en el runner, <c>TEMP</c> viene en 8.3 (<c>RUNNER~1</c>) y la ruta del minivolcado en nombre largo. El nombre
/// largo lo da Windows (<see cref="CrashLongPathName.Expand"/>); la regla sigue calculándose aquí, sin el limpiador.
/// </summary>
internal static class ExpectedDumpPath
{
    public static string Of(string dump) =>
        OperatingSystem.IsWindows() ? Of(dump, Environment.GetEnvironmentVariable, Path.GetTempPath(), CrashLongPathName.Expand) : dump;

    /// <summary>
    /// Con las variables de <paramref name="environment"/> en vez de las del proceso y, si se dan, la carpeta temporal
    /// <paramref name="tempPath"/> y la expansión a nombre largo <paramref name="longPath"/>.
    /// </summary>
    public static string Of(string dump, Func<string, string?> environment, string? tempPath = null, Func<string, string>? longPath = null)
    {
        string[] temps = [Folder(environment, "TEMP"), Folder(environment, "TMP"), Trimmed(tempPath)];
        (string Folder, string Token)[] folders =
        [
            .. temps.Select(t => (t, "%TEMP%")),
            .. temps.Where(t => t.Length > 0 && longPath is not null).Select(t => (Trimmed(longPath!(t)), "%TEMP%")),
            (Folder(environment, "LOCALAPPDATA"), "%LOCALAPPDATA%"),
            (Folder(environment, "USERPROFILE"), "%USERPROFILE%"),
            (ProfileOf(dump), "%USERPROFILE%"),
        ];
        (string folder, string token) = folders
            .Where(f => f.Folder.Length >= 3 && dump.StartsWith(f.Folder + @"\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Folder.Length)
            .FirstOrDefault();
        Assert.True(folder is not null, $"el minivolcado no está en una carpeta del usuario: {dump}");
        string rest = dump[(folder.Length + 1)..];
        int firstSeparator = rest.IndexOf('\\', StringComparison.Ordinal);
        if (token == "%TEMP%" && firstSeparator > 0 && !rest.StartsWith("rodaje-", StringComparison.OrdinalIgnoreCase))
        {
            return token + @"\…\" + rest[(rest.LastIndexOf('\\') + 1)..];
        }

        return token + @"\" + rest;
    }

    private static string Folder(Func<string, string?> environment, string variable) => Trimmed(environment(variable));

    private static string Trimmed(string? folder) => (folder ?? string.Empty).Trim().TrimEnd('\\', '/');

    // «X:\Users\<nombre>» al principio de la ruta.
    private static string ProfileOf(string path)
    {
        string[] parts = path.Split('\\');
        return parts.Length > 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\\', parts[..3])
            : string.Empty;
    }
}
