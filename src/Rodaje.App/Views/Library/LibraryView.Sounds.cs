using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Voice;
using Rodaje.App.Views.Library.Logic;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Views.Library;

/// <summary>Sonidos de la categoría Audio: escucharlos antes, añadirlos en el cursor o arrastrarlos a la línea de tiempo.</summary>
internal sealed partial class LibraryView
{
    private VoicePreviewPlayer? _soundPreview;
    private string? _soundPlaying;

    /// <summary>«Escuchar» o «Detener» y «Añadir en el cursor» en el menú de un sonido.</summary>
    private void AddSoundItems(MenuFlyout menu, LibraryElement element)
    {
        bool playing = _soundPlaying == element.Reference && _soundPreview is { IsPlaying: true };
        (string text, string glyph) = LibrarySoundCommands.Preview(playing);
        menu.Items.Add(MenuItem(text, glyph, (_, _) => ToggleSoundPreview(element)));
        menu.Items.Add(MenuItem("Añadir en el cursor", "", (_, _) => AddSoundAtCursor(element)));
    }

    private void OnSoundPreviewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ElementItem item } button || item.Element.Kind != ItemKind.Sound)
        {
            return;
        }

        ToggleSoundPreview(item.Element);
        bool playing = _soundPlaying == item.Element.Reference && _soundPreview is { IsPlaying: true };
        (string text, string glyph) = LibrarySoundCommands.Preview(playing);
        button.Content = glyph;
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void OnSoundPreviewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;

    private void ToggleSoundPreview(LibraryElement element)
    {
        if (_services is not { } services)
        {
            return;
        }

        _soundPreview ??= new VoicePreviewPlayer();
        if (_soundPlaying == element.Reference && _soundPreview.IsPlaying)
        {
            _soundPreview.Stop();
            _soundPlaying = null;
            return;
        }

        if (SoundDrag.Resolve(services.Packages.Installed, element.Reference, out string? problem) is not { } sound)
        {
            services.Session.Notify(NotificationSeverity.Warning, "No se puede escuchar", problem ?? element.Name);
            return;
        }

        try
        {
            using FileStream stream = File.OpenRead(sound.FilePath);
            (SoundInfo info, float[] samples) = ContentReaders.ReadSound(stream);
            _soundPreview.Play(samples, info.Channels, info.SampleRate);
            _soundPlaying = element.Reference;
            if (_soundPreview.Problem is { } device)
            {
                services.Session.Notify(NotificationSeverity.Warning, "Escuchar", device);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageContentException)
        {
            services.Session.Notify(NotificationSeverity.Warning, "No se puede escuchar", ex.Message);
        }
    }

    /// <summary>En el cursor, en la primera pista de audio libre o en una nueva.</summary>
    private void AddSoundAtCursor(LibraryElement element)
    {
        if (_services is not { } services)
        {
            return;
        }

        EditorSession session = services.Session;
        if (SoundDrag.Resolve(services.Packages.Installed, element.Reference, out string? problem) is not { } sound)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo añadir el sonido", problem ?? element.Name);
            return;
        }

        try
        {
            string folder = GeneratedMedia.Ensure(session.FilePath, services.Settings.Current.GeneratedMediaFolder);
            string copy = SoundDrag.CopyInto(sound, folder);
            Rodaje.Core.Timing.Time at = session.ProgramPosition;
            session.InsertSound(copy, sound, at, SoundDrag.TargetTrack(session.Sequence, null, at, sound.Duration)?.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo añadir el sonido", ex.Message);
        }
    }
}
