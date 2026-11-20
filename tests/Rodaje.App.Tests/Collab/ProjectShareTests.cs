using System.Text;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Model;

namespace Rodaje.App.Tests.Collab;

public sealed class ProjectShareTests
{
    private sealed class FakeSource(Func<ProjectSnapshot> make) : ICollabProjectSource
    {
        public Task<ProjectSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(make());
    }

    private static ProjectSnapshot Snapshot(string? json = null, IReadOnlyList<MediaManifestEntry>? media = null) =>
        new("Proyecto de prueba", Encoding.UTF8.GetBytes(json ?? ProjectWire.ToJson(ModelSamples.ValidProject())), 7, media ?? []);

    [SalaFact]
    public async Task El_invitado_recibe_el_proyecto_sin_rutas_con_sus_versiones_y_los_medios_por_huella()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        MediaManifestEntry entry = new("m1", "toma 1.mp4", 1234, new string('a', 64));
        using ProjectShareHost share = new(host, new FakeSource(() => Snapshot(media: [entry])), host.RoomFolder);

        ReceivedProject received = await ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Proyecto de prueba", received.Name);
        Assert.Equal(7, received.Seq);
        Assert.Equal([entry], received.Media);
        Assert.Equal(2, received.Project.Media.Count);
        Assert.All(received.Project.Media, m =>
        {
            Assert.False(Path.IsPathRooted(m.Path), m.Path);
            Assert.StartsWith(MediaPlaceholders.MediaFolder, m.Path, StringComparison.Ordinal);
        });
        Assert.Equal(SharedState.Capture(ModelSamples.ValidProject()).Hash, SharedState.Capture(received.Project).Hash);
        Assert.Single(Directory.GetFiles(Path.Combine(guest.RoomFolder, "recibido"), "*.rodaje"));
    }

    [SalaFact]
    public async Task Un_anfitrion_que_manda_una_ruta_de_red_en_un_medio_no_entra_en_el_proyecto_del_invitado()
    {
        (RoomService host, RoomService guest, _, _) = await CollabHarness.RoomAsync();
        await using RoomService h = host;
        await using RoomService g = guest;
        string json = ProjectWire.ToJson(ModelSamples.ValidProject());
        string hostile = json.Replace("\"id\":\"m1\"", "\"id\":\"m1\",\"path\":\"\\\\\\\\atacante\\\\recurso\\\\toma.mp4\"", StringComparison.Ordinal);
        Assert.NotEqual(json, hostile);
        using ProjectShareHost share = new(host, new FakeSource(() => Snapshot(hostile)), host.RoomFolder);

        CollabException ex = await Assert.ThrowsAsync<CollabException>(() => ProjectShareGuest.ReceiveAsync(guest, guest.RoomFolder).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Contains("no se puede abrir", ex.Message, StringComparison.Ordinal);
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
            : Snapshot("esto no es un proyecto")), host.RoomFolder);

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

    [Fact]
    public void Un_id_con_caracteres_raros_no_cuela_una_ruta()
    {
        MediaRef m = new() { Id = @"..\..\Windows\x:y" };
        string placeholder = MediaPlaceholders.PlaceholderFor(m, 3);
        Assert.False(Path.IsPathRooted(placeholder));
        Assert.DoesNotContain("..", placeholder, StringComparison.Ordinal);
        Assert.Equal(MediaPlaceholders.MediaFolder + Path.DirectorySeparatorChar + "Windowsxy", placeholder);
    }
}
