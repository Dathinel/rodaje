using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Services.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Recuperar en una carpeta elegida un proyecto con referencias <c>file:</c> relativas (tarea 230): son relativas a la
/// carpeta del proyecto que se cayó, y <see cref="AutosaveService"/> no se la pasaba a <c>ForSaving</c>.
/// </summary>
public class AutosaveRecoveryFileReferencesTests
{
    [Fact]
    public void Recuperar_en_otra_carpeta_recalcula_la_LUT_relativa() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();

        // La carpeta original ya no está (se renombró o se desconectó la unidad): se pregunta dónde guardar.
        string original = h.Temp.File(Path.Combine("Original", "Corto.rodaje"));
        string shared = h.Temp.File(Path.Combine("Compartido", "look.cube"));
        Project crashed = h.Session.Project.Clone();
        crashed.Name = "Corto";
        crashed.Sequences[0].VideoTracks[0].Effects.Add(new EffectInstance
        {
            Id = "fx-lut",
            Type = BuiltInEffects.Color,
            Options = { [BuiltInEffects.ColorOptions.Lut] = "file:../Compartido/look.cube" },
        });
        using (AutosaveSession session = AutosaveSession.Begin(h.Root))
        {
            session.Write(crashed, original);
        }

        string chosenFolder = h.Temp.File(Path.Combine("Elegida", "Hondo"));
        Directory.CreateDirectory(chosenFolder);
        h.Dialogs.SavePath = Path.Combine(chosenFolder, "Corto");
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.True(await h.Service.RecoverAsync(found), string.Join("\n", h.Notifications.Select(n => $"{n.Title}: {n.Message}")));

        // Antes se escribía «file:../Compartido/look.cube» junto al proyecto nuevo: apuntaba a «Elegida/Compartido».
        string recovered = Path.Combine(chosenFolder, "Corto.rodaje");
        Assert.Equal(recovered, h.Session.FilePath);
        Project written = ProjectSerializer.Load(recovered);
        string lut = written.Sequences[0].VideoTracks[0].Effects.Single(e => e.Id == "fx-lut").Options[BuiltInEffects.ColorOptions.Lut];
        Assert.Equal(shared, LutReference.ResolveFile(lut, chosenFolder));
    });
}
