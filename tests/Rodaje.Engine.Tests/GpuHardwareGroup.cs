namespace Rodaje.Engine.Tests;

/// <summary>
/// Las pruebas con la GPU y los decodificadores reales corren solas: miden la VRAM de todo el proceso y el tiempo real,
/// y en paralelo con otras clases el PC de Daniel dio picos de 2152 MB con 1434 MB contabilizados por el motor.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuHardwareGroup
{
    public const string Name = "GPU real";
}
