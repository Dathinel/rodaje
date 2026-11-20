using System.Text;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Model;

namespace Rodaje.App.Tests.Collab;

public sealed class ProjectScrubTests
{
    private static EffectInstance MaskWith(string matte) => new()
    {
        Id = "fx-mask",
        Type = BuiltInEffects.Mask,
        Options = { [BuiltInEffects.MaskOptions.Matte] = matte },
    };

    [Fact]
    public void Quita_las_rutas_de_los_medios_y_los_archivos_sueltos_de_los_efectos()
    {
        Project p = ModelSamples.ValidProject();
        p.Media[0].Path = @"\\equipo-ajeno\recurso\toma.mp4";
        p.Media[1].Path = @"C:\Users\Alguien\Música\fondo.wav";
        Clip clip = p.Sequences.SelectMany(s => s.VideoTracks).SelectMany(t => t.Clips).First();
        clip.Effects.Add(MaskWith("file:\\\\equipo-ajeno\\recurso\\mascara.mp4"));
        p.Sequences[0].MasterEffects.Add(MaskWith("file:C:/Users/Alguien/m.mp4"));

        int removed = ProjectScrub.Scrub(p);

        Assert.Equal(2, removed);
        Assert.All(p.Media, m =>
        {
            Assert.False(Path.IsPathRooted(m.Path), m.Path);
            Assert.StartsWith(ProjectScrub.MediaFolder + Path.DirectorySeparatorChar, m.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("equipo-ajeno", m.Path, StringComparison.Ordinal);
        });
        Assert.Empty(clip.Effects.Single(e => e.Id == "fx-mask").Options);
        Assert.Empty(p.Sequences[0].MasterEffects.Single(e => e.Id == "fx-mask").Options);
        string json = ProjectSerializer.Serialize(p);
        Assert.DoesNotContain("equipo-ajeno", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Alguien", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Un_id_con_caracteres_raros_no_cuela_una_ruta()
    {
        MediaRef m = new() { Id = @"..\..\Windows\x:y" };
        string placeholder = ProjectScrub.PlaceholderFor(m, 3);
        Assert.False(Path.IsPathRooted(placeholder));
        Assert.DoesNotContain("..", placeholder, StringComparison.Ordinal);
        Assert.Equal(ProjectScrub.MediaFolder + Path.DirectorySeparatorChar + "Windowsxy", placeholder);
    }
}

public sealed class ProjectShareTests
{
    private sealed class FakeSource(Func<ProjectSnapshot> make) : ICollabProjectSource
    {
        public Task<ProjectSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(make());
    }

    private static ProjectSnapshot Snapshot(Project p, IReadOnlyList<MediaManifestEntry>? media = null, bool scrub = true)
    {
        Project copy = ModelSamples.ValidProject();
        copy.Media = p.Media;
        copy.Sequences = p.Sequences;
        if (scrub)
        {
            ProjectScrub.Scrub(copy);
        }

        return new ProjectSnapshot(copy.Name, Encoding.UTF8.GetBytes(ProjectSerializer.Serialize(copy)), 7, media ?? []);
    }

    [SalaFact]
    public async Task El_invitado_recibe_el_proyecto_sin_rutas_y_los_medios_por_huella()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        MediaManifestEntry entry = new("m1", "toma 1.mp4", 1234, new string('a', 64));
        using ProjectShareHost share = new(host, new FakeSource(() => Snapshot(ModelSamples.ValidProject(), [entry])), host.RoomFolder);

        ReceivedProject received = await ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Proyecto de prueba", received.Name);
        Assert.Equal(7, received.Seq);
        Assert.Equal([entry], received.Media);
        Assert.Equal(2, received.Project.Media.Count);
        Assert.All(received.Project.Media, m => Assert.StartsWith(ProjectScrub.MediaFolder, m.Path, StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(Path.Combine(guest.RoomFolder, "recibido"), "*.rodaje"));
    }

    [SalaFact]
    public async Task Un_anfitrion_que_manda_rutas_ajenas_no_las_deja_en_el_proyecto_del_invitado()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        Project hostile = ModelSamples.ValidProject();
        hostile.Media[0].Path = @"\\atacante\recurso\toma.mp4";
        hostile.Sequences.SelectMany(s => s.VideoTracks).SelectMany(t => t.Clips).First().Effects.Add(new EffectInstance
        {
            Id = "fx-mask",
            Type = BuiltInEffects.Mask,
            Options = { [BuiltInEffects.MaskOptions.Matte] = "file:\\\\atacante\\recurso\\m.mp4" },
        });
        using ProjectShareHost share = new(host, new FakeSource(() => Snapshot(hostile, scrub: false)), host.RoomFolder);

        ReceivedProject received = await ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain("atacante", ProjectSerializer.Serialize(received.Project), StringComparison.Ordinal);
        Assert.Equal(1, received.DroppedFiles);
    }

    [SalaFact]
    public async Task Si_el_anfitrion_manda_algo_que_no_es_un_proyecto_o_falla_el_invitado_lo_sabe_con_un_texto()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        bool broken = true;
        using ProjectShareHost share = new(host, new FakeSource(() => broken
            ? throw new InvalidOperationException("el disco se rompió")
            : new ProjectSnapshot("X", Encoding.UTF8.GetBytes("esto no es un proyecto"), 0, [])), host.RoomFolder);

        CollabException failed = await Assert.ThrowsAsync<CollabException>(() => ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("No se pudo preparar", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("disco", failed.Message, StringComparison.Ordinal);

        broken = false;
        CollabException garbage = await Assert.ThrowsAsync<CollabException>(() => ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("no se puede abrir", garbage.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mi proyecto", "Mi proyecto")]
    [InlineData(@"..\..\Windows\evil", "evil")]
    [InlineData("a<b>c:d?e", "de")]
    [InlineData("   ", "Proyecto de la sala")]
    [InlineData("con\u0000nulo", "connulo")]
    public void El_nombre_del_archivo_nunca_trae_carpetas_ni_caracteres_prohibidos(string entrada, string esperado) =>
        Assert.Equal(esperado, ProjectShareGuest.ProjectFileName(entrada));
}
