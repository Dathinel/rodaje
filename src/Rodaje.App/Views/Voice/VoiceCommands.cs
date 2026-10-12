using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Voice;

/// <summary>«Texto a voz…» en Editar y en el menú del clip. Sin atajo por omisión.</summary>
internal static class VoiceCommands
{
    public const string TextToSpeech = "voice.textToSpeech";
    public const string Label = "Texto a voz...";
    public const string Glyph = "";

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(TextToSpeech, () => Run(services));
    }

    private static async void Run(EditorServices services)
    {
        try
        {
            await VoiceDialog.ShowAsync(services);
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, "Texto a voz", ex.Message);
        }
    }
}
