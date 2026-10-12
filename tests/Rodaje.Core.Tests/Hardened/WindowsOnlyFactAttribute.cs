namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Prueba que solo tiene sentido en Windows. Fuera de Windows se informa como omitida, con la causa, en lugar de salir
/// en verde sin haber comprobado nada (lo que pasaba con un <c>if (!OperatingSystem.IsWindows()) return;</c>).
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
