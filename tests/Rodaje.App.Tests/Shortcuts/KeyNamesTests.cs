using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class KeyNamesTests
{
    [Theory]
    [InlineData("Ctrl+Mayús+Z", 'Z', KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("ctrl+shift+z", 'Z', KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Control+Mayus+z", 'Z', KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Espacio", 0x20, KeyModifiers.None)]
    [InlineData("Space", 0x20, KeyModifiers.None)]
    [InlineData("Supr", 0x2E, KeyModifiers.None)]
    [InlineData("Retroceso", 0x08, KeyModifiers.None)]
    [InlineData("Alt+[", 0xDB, KeyModifiers.Alt)]
    [InlineData("Alt+]", 0xDD, KeyModifiers.Alt)]
    [InlineData("Ctrl++", 0xBB, KeyModifiers.Control)]
    [InlineData("Ctrl+-", 0xBD, KeyModifiers.Control)]
    [InlineData("+", 0xBB, KeyModifiers.None)]
    [InlineData("Ctrl+Num +", 0x6B, KeyModifiers.Control)]
    [InlineData("Num 5", 0x65, KeyModifiers.None)]
    [InlineData("NumPad5", 0x65, KeyModifiers.None)]
    [InlineData("F12", 0x7B, KeyModifiers.None)]
    [InlineData("Mayús+F1", 0x70, KeyModifiers.Shift)]
    [InlineData("←", 0x25, KeyModifiers.None)]
    [InlineData("Izquierda", 0x25, KeyModifiers.None)]
    [InlineData("Alt+→", 0x27, KeyModifiers.Alt)]
    [InlineData("Inicio", 0x24, KeyModifiers.None)]
    [InlineData("RePág", 0x21, KeyModifiers.None)]
    [InlineData("Win+Alt+K", 'K', KeyModifiers.Windows | KeyModifiers.Alt)]
    [InlineData("  ctrl + s  ", 'S', KeyModifiers.Control)]
    [InlineData("7", '7', KeyModifiers.None)]
    public void TryParse_EntiendeEspañolYFormatoDeArchivo(string text, int key, KeyModifiers modifiers)
    {
        Assert.True(KeyChord.TryParse(text, out KeyChord chord));
        Assert.Equal(new KeyChord(key, modifiers), chord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Mayús")]
    [InlineData("Ctrl+Hiper")]
    [InlineData("F25")]
    [InlineData("AB")]
    public void TryParse_RechazaLoQueNoEsUnaCombinacion(string? text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Fact]
    public void Parse_FallaConMensajeEnEspañol()
    {
        FormatException error = Assert.Throws<FormatException>(() => KeyChord.Parse("Ctrl+Hiper"));
        Assert.Contains("Ctrl+Hiper", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('Z', KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Mayús+Z", "Ctrl+Shift+Z")]
    [InlineData(0x20, KeyModifiers.None, "Espacio", "Space")]
    [InlineData(0x2E, KeyModifiers.None, "Supr", "Delete")]
    [InlineData(0xBB, KeyModifiers.Control, "Ctrl++", "Ctrl+OemPlus")]
    [InlineData(0x25, KeyModifiers.Alt, "Alt+←", "Alt+Left")]
    [InlineData(0x65, KeyModifiers.None, "Num 5", "NumPad5")]
    [InlineData(0x7B, KeyModifiers.Windows, "Win+F12", "Win+F12")]
    [InlineData(0xFF, KeyModifiers.None, "VKFF", "VKFF")]
    public void Format_DaEspañolYFormatoEstable(int key, KeyModifiers modifiers, string display, string invariant)
    {
        var chord = new KeyChord(key, modifiers);
        Assert.Equal(display, chord.ToString());
        Assert.Equal(invariant, chord.ToInvariantString());
    }

    [Fact]
    public void TodasLasTeclasConNombre_VanYVuelven()
    {
        KeyModifiers[] combos = [KeyModifiers.None, KeyModifiers.Control, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt];
        for (int key = 0x08; key <= 0xE2; key++)
        {
            if (KeyChord.IsModifierKey(key) || KeyNames.KeyName(key).StartsWith("VK", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (KeyModifiers modifiers in combos)
            {
                var chord = new KeyChord(key, modifiers);
                Assert.True(KeyChord.TryParse(chord.ToString(), out KeyChord fromDisplay), chord.ToString());
                Assert.Equal(chord, fromDisplay);
                Assert.True(KeyChord.TryParse(chord.ToInvariantString(), out KeyChord fromFile), chord.ToInvariantString());
                Assert.Equal(chord, fromFile);
            }
        }
    }

    [Fact]
    public void Fold_QuitaTildesYMayusculas()
    {
        Assert.Equal("mayus edicion ano", KeyNames.Fold("Mayús Edición AÑO"));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0xA0)]
    [InlineData(0xA5)]
    public void IsModifierKey_ReconoceLosModificadores(int key) => Assert.True(KeyChord.IsModifierKey(key));
}
