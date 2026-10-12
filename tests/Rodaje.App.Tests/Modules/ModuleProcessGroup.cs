namespace Rodaje.App.Tests.Modules;

/// <summary>Pruebas con procesos de módulo que miden tiempos cortos: corren solas, después del resto del proyecto.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModuleProcessGroup
{
    public const string Name = "Procesos de módulos";
}
