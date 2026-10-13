using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Format;

/// <summary>
/// La guarda de <see cref="EditorSession.ChangeFormat"/> (tarea 91): un tamaño o una cadencia imposibles no llegan al
/// comando (que lanza <see cref="ArgumentException"/> y cerraría la app desde un clic); se avisa y la secuencia y el
/// historial quedan como estaban.
/// </summary>
public class ChangeFormatGuardTests
{
    private static EditorSession Session() =>
        new(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());

    private static void AssertRejected(int width, int height, Rational frameRate)
    {
        EditorSession session = Session();
        Sequence sequence = session.Sequence;
        (int w, int h, Rational fps) = (sequence.Width, sequence.Height, sequence.FrameRate);
        var notes = new List<EditorNotification>();
        session.Notified += (_, n) => notes.Add(n);

        Assert.False(session.ChangeFormat(width, height, frameRate, ClipFit.Fit));

        Assert.Equal((w, h, fps), (session.Sequence.Width, session.Sequence.Height, session.Sequence.FrameRate));
        Assert.False(session.CanUndo);
        EditorNotification note = Assert.Single(notes);
        Assert.Equal(NotificationSeverity.Warning, note.Severity);
        Assert.Equal("Formato del proyecto", note.Title);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(0, 0)]
    [InlineData(-1920, 1080)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void TamanoCeroONegativo_NoLlegaAlComando(int width, int height) => AssertRejected(width, height, Rational.Fps30);

    [Theory]
    [InlineData(1921, 1080)]
    [InlineData(1920, 1081)]
    [InlineData(1079, 1919)]
    [InlineData(ProjectFormats.MinSide + 1, ProjectFormats.MinSide)]
    public void TamanoImpar_NoLlegaAlComando(int width, int height) => AssertRejected(width, height, Rational.Fps30);

    [Theory]
    [InlineData(ProjectFormats.MinSide - 2, 1080)]
    [InlineData(1920, ProjectFormats.MaxSide + 2)]
    public void TamanoFueraDeLosLimites_NoLlegaAlComando(int width, int height) => AssertRejected(width, height, Rational.Fps30);

    [Fact]
    public void CadenciaCero_NoLlegaAlComando()
    {
        // Rational no deja construir una tasa ≤ 0: la única que puede llegar es la de por omisión (0/0).
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(-30, 1));
        AssertRejected(1080, 1920, default);
    }

    [Fact]
    public void CadenciaCero_ConElMismoTamano_TambienSeRechaza()
    {
        EditorSession session = Session();
        AssertRejected(session.Sequence.Width, session.Sequence.Height, default);
    }

    [Fact]
    public void UnFormatoValido_SiLlegaAlComando()
    {
        // La guarda no rechaza de más: los límites y una cadencia normal pasan, en un paso del historial.
        EditorSession session = Session();
        Assert.True(session.ChangeFormat(ProjectFormats.MinSide, ProjectFormats.MaxSide, Rational.Ntsc24, ClipFit.Fit));
        Assert.Equal((ProjectFormats.MinSide, ProjectFormats.MaxSide, Rational.Ntsc24), (session.Sequence.Width, session.Sequence.Height, session.Sequence.FrameRate));
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void ElMismoFormato_NoHaceNada_NiAvisa()
    {
        EditorSession session = Session();
        int notes = 0;
        session.Notified += (_, _) => notes++;
        Assert.False(session.ChangeFormat(session.Sequence.Width, session.Sequence.Height, session.Sequence.FrameRate, ClipFit.Fit));
        Assert.Equal(0, notes);
        Assert.False(session.CanUndo);
    }
}
