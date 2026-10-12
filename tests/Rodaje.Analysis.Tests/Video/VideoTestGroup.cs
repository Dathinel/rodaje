namespace Rodaje.Analysis.Tests.Video;

/// <summary>
/// El análisis de imagen reparte el trabajo entre todos los núcleos: sus pruebas corren solas, después del resto, para no
/// falsear las medidas de tiempo de las demás (ni las suyas).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VideoTestGroup
{
    public const string Name = "Análisis de imagen";
}
