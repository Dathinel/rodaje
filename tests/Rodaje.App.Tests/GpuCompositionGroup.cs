namespace Rodaje.App.Tests;

/// <summary>
/// Pruebas que compilan los shaders del <c>Compositor</c> y componen con WARP en la CPU: corren solas, después del resto
/// del proyecto. En la CI las 75 variantes se compilan desde cero en un hilo y, compartiendo la CPU con las demás clases en
/// paralelo, la espera de 60 s a <c>ShadersReady</c> llegó a vencer.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuCompositionGroup
{
    public const string Name = "Composición en la GPU";
}
