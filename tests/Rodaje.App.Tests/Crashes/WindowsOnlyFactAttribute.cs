namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Prueba que solo tiene sentido en Windows (un proceso hijo con <c>cmd.exe</c>, <c>MiniDumpWriteDump</c>). Fuera de
/// Windows se informa como omitida, con la causa, en lugar de fallar o salir en verde sin comprobar nada.
/// </summary>
internal sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Solo Windows: {because}";
        }
    }
}
