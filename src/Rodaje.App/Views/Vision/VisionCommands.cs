using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Vision;

/// <summary>«Quitar fondo», «Reencuadre automático» y las pestañas de máscaras y seguimiento: barra de herramientas y menú del clip. Sin atajo por omisión.</summary>
internal static partial class VisionCommands
{
    public const string RemoveBackground = "vision.removeBackground";
    public const string Reframe = "vision.reframe";
    public const string Masks = "vision.masks";
    public const string Tracking = "vision.tracking";

    public static IReadOnlyList<(string Id, string Label, string Glyph)> Menu { get; } =
    [
        (RemoveBackground, "Quitar fondo...", "\uE7A8"),
        (Reframe, "Reencuadre automático...", "\uE7AD"),
        (Masks, "Máscaras", "\uE71C"),
    ];

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        IDisposable[] bindings =
        [
            router.Bind(RemoveBackground, () => Run(services, RemoveBackground, VisionDialogs.RemoveBackgroundTitle,
                scope => VisionDialogs.RemoveBackgroundAsync(services, scope))),
            router.Bind(Reframe, () => Run(services, Reframe, VisionDialogs.ReframeTitle,
                scope => VisionDialogs.ReframeAsync(services, scope))),
            router.Bind(Masks, services.Session.ShowMaskPanel),
            router.Bind(Tracking, services.Session.ShowTrackPanel),
        ];
        return new Bindings(bindings);
    }

    private static void Run(EditorServices services, string id, string title, Func<LongActionScope, Task> work) =>
        UiTask.Run(services.LongActions.RunAsync(id, title, work, canCancel: true));

    private sealed partial class Bindings(IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable item in items)
            {
                item.Dispose();
            }
        }
    }
}
