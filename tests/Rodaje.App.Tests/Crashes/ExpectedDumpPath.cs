namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// La ruta del minivolcado tal como debe salir en el informe, calculada sin el limpiador (antes las pruebas comparaban con
/// el propio limpiador y no podían fallar). Las pruebas lo dejan en la carpeta temporal: en Windows, la carpeta del
/// usuario más larga que la contiene (<c>TEMP</c> puede venir con el nombre corto 8.3 y la ruta con el largo) pasa a su
/// variable y el resto, que cuelga de <c>AppData</c>, queda intacto; fuera de Windows no hay carpetas del usuario. Dentro
/// de <c>%TEMP%</c> solo quedan a la vista las carpetas <c>rodaje-…</c>: las demás salen como <c>%TEMP%\…\archivo</c>.
/// </summary>
internal static class ExpectedDumpPath
{
    public static string Of(string dump) =>
        OperatingSystem.IsWindows() ? Of(dump, Environment.GetEnvironmentVariable) : dump;

    /// <summary>Con las variables de <paramref name="environment"/> en vez de las del proceso.</summary>
    public static string Of(string dump, Func<string, string?> environment)
    {
        (string Folder, string Token)[] folders =
        [
            (Folder(environment, "TEMP"), "%TEMP%"),
            (Folder(environment, "TMP"), "%TEMP%"),
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

    private static string Folder(Func<string, string?> environment, string variable) =>
        (environment(variable) ?? string.Empty).Trim().TrimEnd('\\', '/');

    // «X:\Users\<nombre>» al principio de la ruta.
    private static string ProfileOf(string path)
    {
        string[] parts = path.Split('\\');
        return parts.Length > 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\\', parts[..3])
            : string.Empty;
    }
}
