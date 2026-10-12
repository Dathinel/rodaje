using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Completa el punto de conexión de <c>Shell/EditorServices.cs</c> con el contenido de <c>Services/Effects</c>.</summary>
internal static partial class EffectContentFactory
{
    static partial void Create(PackageService packages, ref IEffectContent? content)
    {
        EffectContent created = AppEffectContent.Create(packages);
        created.StabilizationSource = Views.Analysis.StabilizationStore.Default.Find;
        content = created;
    }
}
