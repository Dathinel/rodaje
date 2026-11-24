namespace Rodaje.App.Views.Raw;

internal enum CinemaDngDevelopPath
{
    Cpu,
    Gpu,
    PendingFrame,
}

/// <summary>Camino de revelado y opciones que siguen siendo útiles antes de leer el primer cuadro.</summary>
internal sealed record CinemaDngDevelopChoice(CinemaDngDevelopPath Path, bool OfferQuality)
{
    public string Description => Path switch
    {
        CinemaDngDevelopPath.Cpu => "Se revela en la CPU con LibRaw. ",
        CinemaDngDevelopPath.Gpu => "Se revela en la GPU si se puede crear el revelador; si no, CPU. ",
        _ => "GPU si el módulo y el cuadro lo permiten; si no, CPU. ",
    };

    public static CinemaDngDevelopChoice Decide(bool hasHardwareGpu, bool moduleOffersMosaic, bool? isBayer)
    {
        CinemaDngDevelopPath path = !hasHardwareGpu || !moduleOffersMosaic || isBayer == false
            ? CinemaDngDevelopPath.Cpu
            : isBayer is null ? CinemaDngDevelopPath.PendingFrame : CinemaDngDevelopPath.Gpu;
        return new(path, path != CinemaDngDevelopPath.Gpu);
    }
}
