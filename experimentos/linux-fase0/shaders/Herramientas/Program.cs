using Fase0.Shaders;

string salida = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Tool.FolderRoot(), "salida");
switch (args.FirstOrDefault())
{
    case "compilar":
        return Compile.Run(salida);
    case "referencia":
        return Reference.Run(salida);
    case "referencia-gpu":
        return Reference.Run(salida, hardware: true);
    default:
        Console.Error.WriteLine("Uso: fase0-shaders compilar|referencia|referencia-gpu [carpeta de salida]");
        return 2;
}

internal static class Tool
{
    /// <summary>La carpeta <c>experimentos/linux-fase0/shaders</c>, buscada hacia arriba desde el ejecutable.</summary>
    public static string FolderRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ci.ps1")) && Directory.Exists(Path.Combine(dir.FullName, "Herramientas")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
