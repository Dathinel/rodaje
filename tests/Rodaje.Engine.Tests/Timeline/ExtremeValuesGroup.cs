namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Las pruebas de valores extremos recorren todo el registro y gastan CPU: corren solas, después de las paralelas, para
/// no quitarles tiempo a las de estrés (que fallan si un hilo no termina en 60 s con el runner cargado).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExtremeValuesGroup
{
    public const string Name = "Valores extremos";
}
