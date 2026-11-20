namespace Rodaje.Core.Collab;

/// <summary>Resultado de leer un parche de la red.</summary>
public sealed record PatchParseResult(Patch? Patch, PatchRejection? Rejection)
{
    public bool IsValid => Patch is not null;
}

/// <summary>Un parche como texto JSON de red (<c>docs/propuestas/colaboracion-api-parches.md</c>).</summary>
public static class PatchWire
{
    /// <summary>El JSON del parche, compacto y con claves en orden fijo.</summary>
    public static string ToJson(Patch patch) => throw Pending.Exception();

    /// <summary>
    /// Lee un parche que llegó de otro equipo: pasa antes por <see cref="WireSanitizer"/> y nunca lanza. Un texto hostil o mal
    /// formado devuelve el rechazo, no una excepción.
    /// </summary>
    public static PatchParseResult TryParse(string json, WireLimits? limits = null) => throw Pending.Exception();
}
