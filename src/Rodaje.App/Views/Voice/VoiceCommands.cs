using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Voice;

/// <summary>«Texto a voz…» en Editar y en el menú del clip. Sin atajo por omisión.</summary>
internal static partial class VoiceCommands
{
    public const string TextToSpeech = "voice.textToSpeech";
    public const string Label = "Texto a voz...";

    /// <summary>«Grabar voz en off…»: en la barra de herramientas y, con su pista, en la cabecera de cada pista de audio.</summary>
    public const string VoiceOver = "audio.voiceOver";
    public const string Glyph = "";

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        IDisposable speech = router.Bind(TextToSpeech, () => Run(services));
        IDisposable voiceOver = router.Bind(VoiceOver, () => RecordVoiceOver(services, null));
        return new Both(speech, voiceOver);
    }

    /// <summary>Abre «Grabar voz en off» con la pista elegida (nula: pista nueva).</summary>
    public static async void RecordVoiceOver(EditorServices services, string? trackId)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            await VoiceOverDialog.ShowAsync(services, trackId);
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, "Grabar voz en off", ex.Message);
        }
    }

    private sealed partial class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
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
