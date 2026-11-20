using System.Text;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.App.Services.Collab;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Collab;

public sealed class CollabRecoveryTests
{
    private static Patch AddMarker(Project project, string id = "pendiente")
    {
        Project after = project.Clone();
        after.Sequences[0].Markers.Add(new Marker { Id = id, Name = "Cambio conservado", Time = Time.FromSeconds(2) });
        return PatchDiff.Create(SharedState.Capture(project), SharedState.Capture(after), new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, "propio", "Añadir marcador"))!;
    }

    private static async Task JoinThird(Replica host, Replica third)
    {
        CollabInvitation invite = await host.Room.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = third.Room.JoinAsync(invite.Link, "Eva", CollabHarness.TempFolder());
        await CollabHarness.Until(() => host.Room.State.Requests.Count == 1, "la solicitud de Eva");
        await host.Room.ApproveAsync(host.Room.State.Requests[0].Key, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        (string json, long seq) = host.Engine.CaptureForGuest();
        ProjectParseResult parsed = ProjectWire.TryParse(json);
        Assert.True(parsed.IsValid);
        third.Attach(parsed.Project!);
        await third.Ui.Post(() => third.Engine.StartAsGuest(parsed.Project!.Clone(), parsed.Versions!, seq));
    }

    [SalaFact]
    public async Task El_pendiente_conserva_el_id_durante_un_traspaso()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        TaskCompletionSource validating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.BeforeValidation(async ct => { validating.TrySetResult(); await release.Task.WaitAsync(ct); });
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "durante-traspaso", Name = "En espera", Time = Time.FromSeconds(2) }));
        await validating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        JournalEntry pending = Assert.Single(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries.Where(e => e.Kind == "pending"));
        Task handover = a.Room.HandOverAsync(b.Room.State.MyKey);
        release.TrySetResult();
        await handover.WaitAsync(TimeSpan.FromSeconds(30));
        await CollabHarness.Until(() => b.Room.State.IsHost && a.Engine.CaptureForGuest() == b.Engine.CaptureForGuest()
            && b.Engine.CaptureForGuest().Seq == 1, "el pendiente confirmado tras pasar el mando");
        await CollabHarness.Until(() => new CollabJournal(b.Room.RoomFolder).Load(() => { })?.Accepted.Contains(pending.Patch.Id) == true, "el mismo id confirmado");
        Assert.Single(b.Live.Sequences[0].Markers.Where(m => m.Id == "durante-traspaso"));
        Assert.Empty(a.Desyncs);
        Assert.Empty(b.Desyncs);
    }

    [SalaFact]
    public async Task El_suplente_recupera_confirmados_y_el_pendiente_tras_dispose_del_anfitrion()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await using Replica c = new(standbyWait: TimeSpan.FromMilliseconds(100));
        await JoinThird(a, c);
        await a.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "confirmado", Name = "Confirmado", Time = Time.FromSeconds(1) }));
        await CollabHarness.Until(() => b.Engine.CaptureForGuest().Seq == 1 && c.Engine.CaptureForGuest().Seq == 1, "lo confirmado en ambos invitados");
        await CollabHarness.Until(() => b.Room.StandbyKey == b.Room.State.MyKey, "B es suplente");

        TaskCompletionSource validating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.BeforeValidation(async ct => { validating.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "sin-respuesta", Name = "Sin respuesta", Time = Time.FromSeconds(3) }));
        await validating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await a.Room.LeaveAsync(); // Dispose de Sala; sin mandar un cierre de edición a los invitados.
        await CollabHarness.Until(() => b.Room.State.Phase == RoomPhase.Reconnecting, "B pierde al anfitrión");
        await Task.Delay(150);
        await Assert.ThrowsAsync<CollabException>(() => c.Room.TakeHostAsync());
        await b.Room.TakeHostAsync();
        await CollabHarness.Until(() => b.Room.State.IsHost && c.Room.State.Phase == RoomPhase.Connected, "B toma la sala y C lo encuentra", 40);
        await CollabHarness.Until(() => b.Engine.CaptureForGuest() == c.Engine.CaptureForGuest(), "los confirmados convergen");
        Assert.Contains(b.Live.Sequences[0].Markers, m => m.Id == "confirmado");
        await CollabHarness.Until(() => b.Live.Sequences[0].Markers.Any(m => m.Id == "sin-respuesta")
            && b.Engine.CaptureForGuest().Seq == 2 || b.Engine.Unsettled.Items.Count > 0, "el pendiente entra o queda recuperable");
        Assert.True(b.Engine.CaptureForGuest().Seq == 2 || b.Engine.Unsettled.Items.Any(i => !string.IsNullOrWhiteSpace(i.Reason)));
        Assert.Empty(b.Desyncs);
        Assert.Empty(c.Desyncs);
    }

    [SalaFact]
    public async Task Un_pendiente_se_escribe_antes_de_validarse_y_reabrir_reenvia_su_mismo_id()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        Project confirmed = b.Live.Clone();
        TaskCompletionSource validating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.BeforeValidation(async ct => { validating.TrySetResult(); await release.Task.WaitAsync(ct); });
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "reinicio", Name = "Tras reiniciar", Time = Time.FromSeconds(4) }));
        await validating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        CollabJournal journal = new(b.Room.RoomFolder);
        JournalState saved = journal.Load(() => Assert.Fail("registro inválido"))!;
        JournalEntry pending = Assert.Single(saved.Entries.Where(e => e.Kind == "pending"));
        Assert.Equal(0, saved.Seq);
        Assert.DoesNotContain(b.Room.State.MyKey, File.ReadAllText(Path.Combine(b.Room.RoomFolder, "registro.jsonl"), Encoding.UTF8), StringComparison.Ordinal);

        // Reabre el motor sobre el mismo registro; Sala aún puede traer la respuesta del envío anterior.
        await b.Ui.Post(() =>
        {
            b.Engine.Dispose();
            b.Attach(confirmed.Clone());
            b.Engine.StartAsGuest(confirmed.Clone(), new ElementVersions(), 0);
        });
        release.TrySetResult();
        await CollabHarness.Until(() => b.Engine.CaptureForGuest().Seq >= 1 && a.Engine.CaptureForGuest() == b.Engine.CaptureForGuest(), "el pendiente vuelve y se deduplica");
        Assert.Single(b.Live.Sequences[0].Markers.Where(m => m.Id == "reinicio"));
        await CollabHarness.Until(() => journal.Load(() => { })?.Entries.All(e => e.Kind != "pending") == true, "el pendiente retirado del registro");
        JournalState settled = journal.Load(() => Assert.Fail("registro inválido"))!;
        Assert.Contains(pending.Patch.Id, settled.Accepted);
        Assert.Empty(settled.Entries.Where(e => e.Kind == "pending"));
        Assert.Empty(b.Desyncs);
    }

    [Fact]
    public void El_registro_acota_y_rechaza_datos_hostiles_sin_lanzar()
    {
        string folder = CollabHarness.TempFolder();
        CollabJournal journal = new(folder);
        Project project = ModelSamples.ValidProject();
        Patch patch = AddMarker(project);
        JournalState state = new(0, ProjectWire.ToJson(project), null, [], [new JournalEntry("pending", patch)]);
        Assert.True(journal.Save(state));
        Assert.Equal(patch.Id, Assert.Single(journal.Load(() => Assert.Fail("registro válido"))!.Entries).Patch.Id);
        Assert.False(journal.Save(state with { Entries = Enumerable.Repeat(new JournalEntry("pending", patch), CollabJournal.MaxEntries + 1).ToArray() }));
        Assert.Equal(patch.Id, Assert.Single(journal.Load(() => Assert.Fail("se conserva el archivo anterior"))!.Entries).Patch.Id);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        string path = Path.Combine(folder, "registro.jsonl");
        int warnings = 0;
        foreach (string hostile in new[] { "{", "[]", "{\"format\":1,\"format\":1}", new string('[', 80) })
        {
            File.WriteAllText(path, hostile, Encoding.UTF8);
            Assert.Null(journal.Load(() => warnings++));
        }
        Assert.Equal(4, warnings);
        Assert.True(journal.Save(state));
        string json = File.ReadAllText(path, Encoding.UTF8).Replace("\"kind\":\"marker\"", "\"kind\":\"desconocido\"", StringComparison.Ordinal);
        File.WriteAllText(path, json, Encoding.UTF8);
        Assert.Null(journal.Load(() => warnings++));
        Assert.Equal(5, warnings);
        using (FileStream oversized = new(path, FileMode.Create, FileAccess.Write)) oversized.SetLength(CollabJournal.MaxBytes + 1L);
        Assert.Null(journal.Load(() => warnings++));
        Assert.Equal(6, warnings);
        const string reason = "Otra persona tiene la pista reservada. Puedes conservar tu cambio o descartarlo.";
        Assert.True(journal.Save(state with { Entries = [new JournalEntry("unsettled", patch, Reason: reason)] }));
        Assert.Equal(reason, Assert.Single(journal.Load(() => Assert.Fail("registro válido"))!.Entries).Reason);
    }

    [SalaFact]
    public async Task Conservar_crea_ids_nuevos_y_descartar_no_cambia_el_proyecto()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        Assert.True(await a.Room.ReserveAsync(PatchReservations.Project));
        await CollabHarness.Until(() => b.Room.Reservations.ContainsKey(PatchReservations.Project), "la reserva ajena");
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "no-entro", Name = "Recuperar", Time = Time.FromSeconds(4) }));
        await CollabHarness.Until(() => b.Engine.Unsettled.Items.Count == 1, "el cambio guardado");
        UnsettledChange item = Assert.Single(b.Engine.Unsettled.Items);
        Assert.NotEmpty(item.Reason);
        await a.Room.ReleaseAsync(PatchReservations.Project);
        await CollabHarness.Until(() => !b.Room.Reservations.ContainsKey(PatchReservations.Project), "reserva libre");
        bool kept = false;
        await b.Ui.Post(() => kept = b.Engine.KeepUnsettled(item.Id));
        Assert.True(kept);
        await CollabHarness.Until(() => a.Live.Sequences[0].Markers.Any(m => m.Name == "Recuperar"), "la copia nueva en A");
        Assert.DoesNotContain(a.Live.Sequences[0].Markers, m => m.Id == "no-entro");
        Assert.Empty(b.Engine.Unsettled.Items);

        await b.Ui.Post(() => b.Engine.Unsettled.Add(item.Patch, item.Reason));
        string before = b.HashOnUi();
        await b.Ui.Post(() => b.Engine.DiscardUnsettled(item.Id));
        Assert.Equal(before, b.HashOnUi());
        Assert.Empty(b.Engine.Unsettled.Items);
        Assert.Empty(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries.Where(e => e.Kind == "unsettled"));
    }

    [Fact]
    public void Conservar_un_clip_en_pista_ocupada_lo_copia_en_otra_pista()
    {
        Project project = ModelSamples.ValidProject();
        Project changed = project.Clone();
        Track track = changed.Sequences[0].VideoTracks.First(t => t.Clips.Count > 0);
        track.Clips[0].Enabled = !track.Clips[0].Enabled;
        Patch original = PatchDiff.Create(SharedState.Capture(project), SharedState.Capture(changed), new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, "propio", "Cambiar clip"))!;
        Patch copy = UnsettledRecovery.Create(original, project, new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, "propio", "Conservar clip"), new Dictionary<string, string>())!;
        Assert.NotNull(copy);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.DoesNotContain(copy.Ops, o => original.Ops.Any(old => old.Id == o.Id));
        Assert.True(PatchApply.TryApply(project, new ElementVersions(), copy).IsAccepted);
        Track recovered = Assert.Single(project.Sequences[0].VideoTracks.Where(t => t.Name == "Cambios recuperados"));
        Assert.Single(recovered.Clips);
        Assert.NotEqual(track.Id, recovered.Id);
    }

    [SalaFact]
    public async Task Superada_guarda_solo_los_confirmados_desde_la_separacion_y_no_los_fusiona()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "comun", Name = "Común", Time = Time.FromSeconds(1) }));
        await CollabHarness.Until(() => b.Engine.CaptureForGuest().Seq == 1, "el cambio común");
        b.Engine.OnConnectionLost(); // Límite del motor: simula el aviso de separación de Sala sin cortar la red real.
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "separado", Name = "Separado", Time = Time.FromSeconds(2) }));
        await CollabHarness.Until(() => b.Engine.CaptureForGuest().Seq == 2, "lo confirmado en la rama");
        string before = b.HashOnUi();
        await b.Engine.HandleSupersededAsync();
        UnsettledChange item = Assert.Single(b.Engine.Unsettled.Items);
        Assert.Contains(item.Patch.Ops, op => op.Id == "separado");
        Assert.DoesNotContain(item.Patch.Ops, op => op.Id == "comun");
        Assert.Equal(before, b.HashOnUi());
        JournalState saved = new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!;
        Assert.Equal(item.Id, Assert.Single(saved.Entries.Where(e => e.Kind == "unsettled")).Patch.Id);
        bool kept = true;
        await b.Ui.Post(() => kept = b.Engine.KeepUnsettled(item.Id));
        Assert.False(kept); // Hace falta entrar en la sala ganadora antes de conservarlo.
    }

    [SalaFact]
    public async Task El_anfitrion_superado_avisa_al_autor_conectado_de_sus_confirmados_separados()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        a.Engine.OnConnectionLost();
        await b.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "de-b", Name = "De B", Time = Time.FromSeconds(2) }));
        await CollabHarness.Until(() => a.Engine.CaptureForGuest().Seq == 1 && b.Engine.CaptureForGuest().Seq == 1, "el cambio de B en la rama");
        await a.Engine.HandleSupersededAsync();
        await CollabHarness.Until(() => b.Engine.Unsettled.Items.Count == 1, "el autor recibe su cambio separado");
        Assert.Empty(a.Engine.Unsettled.Items);
        Assert.Contains(Assert.Single(b.Engine.Unsettled.Items).Patch.Ops, op => op.Id == "de-b");
        Assert.Single(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries.Where(e => e.Kind == "unsettled"));
    }

    [Fact]
    public void Un_borrado_no_recuperable_conserva_el_parche_original()
    {
        Project project = ModelSamples.ValidProject();
        Patch patch = AddMarker(project);
        Patch deletion = patch with { Ops = patch.Ops.Select(o => o with { Content = (string?)null }).ToArray() };
        UnsettledChanges changes = new();
        changes.Add(deletion, "El borrado no entró. Puedes rehacerlo manualmente o descartarlo.");
        Assert.Null(UnsettledRecovery.Create(deletion, project, new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, "propio", "Conservar"), new Dictionary<string, string>()));
        Assert.Same(deletion, Assert.Single(changes.Items).Patch);
    }
}
