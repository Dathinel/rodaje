using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Timeline;

/// <summary>Línea de tiempo (por ahora, un resumen de las pistas de la secuencia).</summary>
internal sealed partial class TimelineView : UserControl, IEditorView
{
    public TimelineView()
    {
        InitializeComponent();
    }

    public EditorSession? Session { get; private set; }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Session = services.Session;
        Session.ProjectOpened += (_, _) => Refresh();
        Session.ProjectEdited += (_, _) => Refresh();
        Refresh();
        Bindings.Update();
    }

    private void Refresh()
    {
        if (Session is null)
        {
            return;
        }

        Sequence sequence = Session.Sequence;
        IEnumerable<string> tracks = sequence.VideoTracks.AsEnumerable().Reverse().Concat(sequence.AudioTracks)
            .Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name}: {t.Clips.Count} clip{(t.Clips.Count == 1 ? "" : "s")}"));
        SummaryText.Text = string.Join("   ·   ", tracks);
    }
}
