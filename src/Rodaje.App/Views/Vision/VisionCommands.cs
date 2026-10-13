using Rodaje.App.Services;
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
            router.Bind(RemoveBackground, () => Run(services.Session, VisionDialogs.RemoveBackgroundTitle, () => VisionDialogs.RemoveBackgroundAsync(services))),
            router.Bind(Reframe, () => Run(services.Session, VisionDialogs.ReframeTitle, () => VisionDialogs.ReframeAsync(services))),
            router.Bind(Masks, services.Session.ShowMaskPanel),
            router.Bind(Tracking, services.Session.ShowTrackPanel),
        ];
        return new Bindings(bindings);
    }

    private static async void Run(EditorSession session, string title, Func<Task> show)
    {
        try
        {
            await show();
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            session.Notify(NotificationSeverity.Error, title, ex.Message);
        }
    }

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
