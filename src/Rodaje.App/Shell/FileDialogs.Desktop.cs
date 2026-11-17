using System.Diagnostics;

namespace Rodaje.App.Shell;

/// <summary>
/// Diálogos de abrir y guardar en Linux con <c>zenity</c> (GNOME y casi todos los escritorios) o, si no está, <c>kdialog</c>
/// (KDE): son modales y devuelven la ruta, como los de Windows, sin portal ni D-Bus.
/// </summary>
// shortcut: la ventana no se repinta mientras el diálogo está abierto (la llamada espera en el hilo de la interfaz, como
// en Windows); si hace falta, pasar a los selectores asíncronos del portal de escritorio.
internal static partial class FileDialogs
{
    /// <summary>Rutas elegidas, o vacío si se canceló.</summary>
    public static IReadOnlyList<string> Open(nint owner, string title, bool multiple, string? folder, params (string Name, string Spec)[] filters)
    {
        _ = owner;
        var zenity = new List<string> { "--file-selection", "--title=" + title, "--separator=\n" };
        if (multiple)
        {
            zenity.Add("--multiple");
        }

        AddZenityStart(zenity, folder, null);
        AddZenityFilters(zenity, filters);
        var kdialog = new List<string> { "--title", title, "--getopenfilename", folder ?? Home, KdeFilter(filters) };
        if (multiple)
        {
            kdialog.InsertRange(0, ["--multiple", "--separate-output"]);
        }

        return Run(zenity, kdialog) is { } output
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>Ruta donde guardar, o <c>null</c> si se canceló. Añade <paramref name="defaultExtension"/> si falta.</summary>
    public static string? Save(nint owner, string title, string suggestedName, string defaultExtension, string? folder, params (string Name, string Spec)[] filters)
    {
        _ = owner;
        var zenity = new List<string> { "--file-selection", "--save", "--confirm-overwrite", "--title=" + title };
        AddZenityStart(zenity, folder, suggestedName);
        AddZenityFilters(zenity, filters);
        string start = Path.Combine(folder ?? Home, suggestedName);
        string? path = Run(zenity, ["--title", title, "--getsavefilename", start, KdeFilter(filters)])?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string extension = defaultExtension.StartsWith('.') ? defaultExtension : "." + defaultExtension;
        return Path.HasExtension(path) || extension.Length == 1 ? path : path + extension;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void AddZenityStart(List<string> arguments, string? folder, string? name)
    {
        string start = folder is not null && Directory.Exists(folder) ? folder : Home;
        arguments.Add("--filename=" + Path.Combine(start, name ?? "") + (name is null ? "/" : ""));
    }

    private static void AddZenityFilters(List<string> arguments, (string Name, string Spec)[] filters)
    {
        foreach ((string name, string spec) in filters)
        {
            arguments.Add($"--file-filter={name} | {spec.Replace(';', ' ').Replace("*.*", "*", StringComparison.Ordinal)}");
        }
    }

    private static string KdeFilter((string Name, string Spec)[] filters) =>
        string.Join('\n', filters.Select(f => $"{f.Spec.Replace(';', ' ').Replace("*.*", "*", StringComparison.Ordinal)}|{f.Name}"));

    /// <summary>Corre zenity o, si no está, kdialog; la salida si se aceptó, <c>null</c> si se canceló.</summary>
    /// <exception cref="InvalidOperationException">No hay ninguno de los dos.</exception>
    internal static string? Run(IReadOnlyList<string> zenity, IReadOnlyList<string> kdialog)
    {
        foreach ((string program, IReadOnlyList<string> arguments) in new (string, IReadOnlyList<string>)[] { ("zenity", zenity), ("kdialog", kdialog) })
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException($"No se pudo abrir {program}.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }

            using (process)
            {
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                _ = stderr.Result;
                return process.ExitCode == 0 ? output : null;
            }
        }

        throw new InvalidOperationException("Para elegir archivos hace falta zenity o kdialog (por ejemplo: sudo apt install zenity).");
    }
}
