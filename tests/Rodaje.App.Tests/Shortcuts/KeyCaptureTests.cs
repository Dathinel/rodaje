using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class KeyCaptureTests
{
    private const int Ctrl = 0x11;
    private const int Shift = 0x10;
    private const int Alt = 0x12;
    private const int Esc = 0x1B;

    [Fact]
    public void EmpiezaPidiendoLaCombinacion()
    {
        Assert.Equal("Pulsa la nueva combinación… Esc cancela", new KeyCapture().Message);
    }

    [Fact]
    public void LosModificadoresSolos_EsperanYMuestranLoQueHay()
    {
        var capture = new KeyCapture();

        CaptureResult ctrl = capture.KeyDown(Ctrl, KeyModifiers.None);
        CaptureResult shift = capture.KeyDown(Shift, KeyModifiers.Control);

        Assert.Equal(CaptureStatus.Waiting, ctrl.Status);
        Assert.Equal("Ctrl+… (Esc cancela)", ctrl.Message);
        Assert.Equal("Ctrl+Mayús+… (Esc cancela)", shift.Message);
        Assert.Equal("Ctrl+Mayús+… (Esc cancela)", capture.Message);
    }

    [Fact]
    public void SoltarUnModificador_ActualizaLoQueSeVe()
    {
        var capture = new KeyCapture();
        capture.KeyDown(Ctrl, KeyModifiers.None);
        capture.KeyDown(Shift, KeyModifiers.Control);

        Assert.Equal("Mayús+… (Esc cancela)", capture.KeyUp(Ctrl, KeyModifiers.Control | KeyModifiers.Shift).Message);
        Assert.Equal(KeyCapture.Prompt, capture.KeyUp(Shift, KeyModifiers.Shift).Message);
    }

    [Theory]
    [InlineData('S', KeyModifiers.None)]
    [InlineData('S', KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(0x20, KeyModifiers.None)]
    [InlineData(0x09, KeyModifiers.Control)]
    [InlineData(0x1B, KeyModifiers.Shift)]
    [InlineData(0x73, KeyModifiers.None)]
    [InlineData(0xC0, KeyModifiers.Control | KeyModifiers.Alt)]
    public void UnaTeclaConSusModificadores_SeAcepta(int key, KeyModifiers modifiers)
    {
        CaptureResult result = new KeyCapture().KeyDown(key, modifiers);
        Assert.Equal(CaptureStatus.Accepted, result.Status);
        Assert.Equal(new KeyChord(key, modifiers), result.Chord);
    }

    [Fact]
    public void EscSola_Cancela()
    {
        var capture = new KeyCapture();
        capture.KeyDown(Ctrl, KeyModifiers.None);
        capture.KeyUp(Ctrl, KeyModifiers.Control);
        Assert.Equal(CaptureStatus.Cancelled, capture.KeyDown(Esc, KeyModifiers.None).Status);
    }

    [Theory]
    [InlineData(0x73, KeyModifiers.Alt, "Alt+F4")]
    [InlineData('E', KeyModifiers.Windows, "Windows")]
    [InlineData(0x5B, KeyModifiers.None, "Windows")]
    [InlineData(0x09, KeyModifiers.None, "Tab")]
    [InlineData(0x09, KeyModifiers.Shift, "Tab")]
    [InlineData(0x09, KeyModifiers.Alt, "Alt+Tab")]
    [InlineData(0x1B, KeyModifiers.Control, "Windows")]
    [InlineData(0x1B, KeyModifiers.Control | KeyModifiers.Shift, "Administrador de tareas")]
    [InlineData(0x2E, KeyModifiers.Control | KeyModifiers.Alt, "Windows")]
    [InlineData(0x20, KeyModifiers.Alt, "menú de la ventana")]
    [InlineData(0x79, KeyModifiers.Shift, "menú contextual")]
    [InlineData(0x2C, KeyModifiers.None, "Impr Pant")]
    [InlineData(0x14, KeyModifiers.None, "bloqueo")]
    public void LasCombinacionesReservadas_SeRechazanExplicandoPorQue(int key, KeyModifiers modifiers, string reason)
    {
        var capture = new KeyCapture();
        CaptureResult result = capture.KeyDown(key, modifiers);

        Assert.Equal(CaptureStatus.Rejected, result.Status);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Prueba otra combinación; Esc cancela.", result.Message, StringComparison.Ordinal);
        Assert.True(ReservedKeys.IsReserved(new KeyChord(key, modifiers)));
    }

    [Fact]
    public void TrasUnRechazo_ElMotivoSigueAlSoltarYSePuedeProbarOtra()
    {
        var capture = new KeyCapture();
        capture.KeyDown(Alt, KeyModifiers.None);
        CaptureResult rejected = capture.KeyDown(0x73, KeyModifiers.Alt);

        Assert.Equal(rejected.Message, capture.KeyUp(0x73, KeyModifiers.Alt).Message);
        Assert.Equal(rejected.Message, capture.KeyUp(Alt, KeyModifiers.Alt).Message);
        Assert.Equal(CaptureStatus.Accepted, capture.KeyDown('K', KeyModifiers.Alt).Status);
    }

    [Fact]
    public void ElCatalogoNoUsaCombinacionesReservadas()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            registry.Preset = preset;
            foreach (ShortcutAction action in registry.Actions)
            {
                Assert.All(registry.GetKeys(action.Id), k => Assert.False(ReservedKeys.IsReserved(k), $"{action.Id}: {k}"));
            }
        }
    }
}
