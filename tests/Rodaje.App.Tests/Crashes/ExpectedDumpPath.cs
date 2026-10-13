namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// La ruta del minivolcado tal como debe salir en el informe, calculada sin el limpiador (antes las pruebas comparaban con
/// el propio limpiador y no podían fallar). Las pruebas lo dejan en la carpeta temporal: en Windows, la carpeta del
/// usuario más larga que la contiene (<c>TEMP</c> puede venir con el nombre corto 8.3 y la ruta con el largo) pasa a su
/// variable y el resto, que cuelga de <c>AppData</c>, queda intacto; fuera de Windows no hay carpetas del usuario.
/// </summary>
internal static class ExpectedDumpPath
{
    public static string Of(string dump)
    {
        if (!OperatingSystem.IsWindows())
        {
            return dump;
        }

        (string Folder, string Token)[] folders =
        [
            (Folder("TEMP"), "%TEMP%"),
            (Folder("TMP"), "%TEMP%"),
            (Folder("LOCALAPPDATA"), "%LOCALAPPDATA%"),
            (Folder("USERPROFILE"), "%USERPROFILE%"),
            (ProfileOf(dump), "%USERPROFILE%"),
        ];
        (string folder, string token) = folders
            .Where(f => f.Folder.Length >= 3 && dump.StartsWith(f.Folder + @"\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Folder.Length)
            .FirstOrDefault();
        Assert.True(folder is not null, $"el minivolcado no está en una carpeta del usuario: {dump}");
        return token + dump[folder.Length..];
    }

    private static string Folder(string variable) =>
        (Environment.GetEnvironmentVariable(variable) ?? string.Empty).Trim().TrimEnd('\\', '/');

    // «X:\Users\<nombre>» al principio de la ruta.
    private static string ProfileOf(string path)
    {
        string[] parts = path.Split('\\');
        return parts.Length > 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\\', parts[..3])
            : string.Empty;
    }
}
