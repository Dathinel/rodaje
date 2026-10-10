using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Rodaje.App;

/// <summary>Estado que la interfaz muestra con <c>x:Bind</c> (enlaces compilados, sin reflexión).</summary>
internal sealed partial class PlayerState : INotifyPropertyChanged
{
    public const string PlayGlyphText = "";
    public const string PauseGlyphText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Timecode { get; private set; } = "00:00:00:00 / 00:00:00:00";

    public string PlayGlyph { get; private set; } = PlayGlyphText;

    public string Status { get; private set; } = "Preparando el reproductor...";

    public string MediaName { get; private set; } = "";

    public string ProjectName { get; private set; } = "Sin título";

    public void SetTimecode(string value) => Set(Timecode, value, v => Timecode = v);

    public void SetPlaying(bool playing) => Set(PlayGlyph, playing ? PauseGlyphText : PlayGlyphText, v => PlayGlyph = v);

    public void SetStatus(string value) => Set(Status, value, v => Status = v);

    public void SetMedia(string media, string project)
    {
        Set(MediaName, media, v => MediaName = v, nameof(MediaName));
        Set(ProjectName, project, v => ProjectName = v, nameof(ProjectName));
    }

    private void Set(string current, string value, Action<string> assign, [CallerArgumentExpression(nameof(current))] string name = "")
    {
        if (current == value)
        {
            return;
        }

        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
