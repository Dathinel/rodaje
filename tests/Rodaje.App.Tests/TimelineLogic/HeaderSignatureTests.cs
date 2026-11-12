using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Las cabeceras de pista se rearman cuando cambia lo que muestran, también el tamaño del texto de Preferencias.</summary>
public sealed class HeaderSignatureTests
{
    [Fact]
    public void LaFirmaCambiaConLasPistasElAnchoYElTamanoDelTexto()
    {
        Sequence sequence = ProjectFactory.CreateDefault("p", new SequentialIdGenerator()).Sequences[0];
        Track main = sequence.VideoTracks[0];
        TrackLayout layout = TrackLayout.Build(sequence, main);

        string same = HeaderSignature.Of(layout, magnetic: true, width: 172, fontScale: 1);
        Assert.Equal(same, HeaderSignature.Of(TrackLayout.Build(sequence, main), magnetic: true, width: 172, fontScale: 1));
        Assert.NotEqual(same, HeaderSignature.Of(layout, magnetic: false, width: 172, fontScale: 1));
        Assert.NotEqual(same, HeaderSignature.Of(layout, magnetic: true, width: 200, fontScale: 1));
        Assert.NotEqual(same, HeaderSignature.Of(layout, magnetic: true, width: 172, fontScale: 1.2));

        main.Muted = !main.Muted;
        Assert.NotEqual(same, HeaderSignature.Of(TrackLayout.Build(sequence, main), magnetic: true, width: 172, fontScale: 1));
        sequence.AudioTracks[0].Name = "Voz";
        string renamed = HeaderSignature.Of(TrackLayout.Build(sequence, main), magnetic: true, width: 172, fontScale: 1);
        Assert.Contains("Voz", renamed, StringComparison.Ordinal);
    }
}
