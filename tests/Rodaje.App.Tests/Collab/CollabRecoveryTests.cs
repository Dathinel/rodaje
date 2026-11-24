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
        JournalEntry pending = Assert.Single(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "pending");
        Task handover = a.Room.HandOverAsync(b.Room.State.MyKey);
        release.TrySetResult();
        await handover.WaitAsync(TimeSpan.FromSeconds(30));
        await CollabHarness.Until(() => b.Room.State.IsHost && a.Engine.CaptureForGuest() == b.Engine.CaptureForGuest()
            && b.Engine.CaptureForGuest().Seq == 1, "el pendiente confirmado tras pasar el mando");
        await CollabHarness.Until(() => new CollabJournal(b.Room.RoomFolder).Load(() => { })?.Accepted.Contains(pending.Patch.Id) == true, "el mismo id confirmado");
        Assert.Single(b.Live.Sequences[0].Markers, m => m.Id == "durante-traspaso");
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
        await a.Room.LeaveAsync(warnClosed: false); // Una caída: sin «sala-cerrada» ni cierre de edición a los invitados.
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
        JournalEntry pending = Assert.Single(saved.Entries, e => e.Kind == "pending");
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
        Assert.Single(b.Live.Sequences[0].Markers, m => m.Id == "reinicio");
        await CollabHarness.Until(() => journal.Load(() => { })?.Entries.All(e => e.Kind != "pending") == true, "el pendiente retirado del registro");
        JournalState settled = journal.Load(() => Assert.Fail("registro inválido"))!;
        Assert.Contains(pending.Patch.Id, settled.Accepted);
        Assert.DoesNotContain(settled.Entries, e => e.Kind == "pending");
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
        string author = new('a', 64);
        Assert.True(journal.Save(state with { Entries = [new JournalEntry("separated", patch with { Author = author })], RecoveryHosts = [author] }));
        JournalState restored = journal.Load(() => Assert.Fail("registro válido"))!;
        Assert.Equal(author, Assert.Single(restored.Entries).Patch.Author);
        Assert.Equal(author, Assert.Single(restored.RecoveryHosts!));
        Assert.True(journal.Save(state with { Entries = [new JournalEntry("separated", patch with { Author = "autor-invalido" })] }));
        Assert.Null(journal.Load(() => warnings++));
    }

    [Fact]
    public async Task Un_guardado_del_registro_que_falla_se_reintenta_solo()
    {
        // Un cambio separado quedaba visible pero fuera de registro.jsonl cuando el guardado fallaba una sola vez
        // (el antivirus o el índice de Windows reteniendo el archivo un instante): nada lo volvía a intentar y,
        // sin otro disparo que guardara, el archivo no lo recogía nunca.
        await using RoomService room = new(new CollabEnvironment("sin-red", Path.Combine(CollabHarness.TempFolder(), "id.bin"), "1.0.4"), new LateHandler());
        using CollabPresence presence = new(room);
        using UiLoop ui = new();
        System.Collections.Concurrent.ConcurrentQueue<string> notices = new();
        using CollabEngine engine = new(ModelSamples.ValidProject(), room, presence, ui.Post, () => { }, (text, _) => notices.Enqueue(text));
        int calls = 0;
        engine.SaveJournal = _ => Interlocked.Increment(ref calls) > 2;
        await ui.Post(() => engine.StartAsHost());

        await CollabHarness.Until(() => Volatile.Read(ref calls) >= 3, "el reintento del registro");
        Assert.Contains(notices, n => n.Contains("registro", StringComparison.Ordinal));
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
        Assert.DoesNotContain(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "unsettled");
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
        Track recovered = Assert.Single(project.Sequences[0].VideoTracks, t => t.Name == "Cambios recuperados");
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
        Assert.Equal(item.Id, Assert.Single(saved.Entries, e => e.Kind == "unsettled").Patch.Id);
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
        await CollabHarness.Until(() => new CollabJournal(b.Room.RoomFolder).Load(() => { })?.Entries.Count(e => e.Kind == "unsettled") == 1, "el registro guarda el cambio separado");
    }

    [Fact]
    public void Un_borrado_usa_la_version_y_el_padre_actuales_y_respeta_reservas()
    {
        Project project = ModelSamples.ValidProject();
        Patch deletion = new(Guid.NewGuid().ToString("N"), 0, "propio", "Borrar",
            [new PatchOp(ElementKind.Clip, "c3", "pista-antigua", 0, null)], [], []);
        ElementVersions versions = new();
        versions.Set(new ElementKey(ElementKind.Clip, "c3"), 7);
        PatchHeader header = new(Guid.NewGuid().ToString("N"), 0, "propio", "Conservar");
        Dictionary<string, string> reservations = new() { ["pista:v2"] = "otra-persona" };
        Assert.Null(UnsettledRecovery.Create(deletion, project, versions, header, reservations));
        reservations["pista:v2"] = "propio";
        Patch recovered = Assert.IsType<Patch>(UnsettledRecovery.Create(deletion, project, versions, header, reservations));
        PatchOp op = Assert.Single(recovered.Ops);
        Assert.Equal(7, op.BaseVersion);
        Assert.Equal("v2", op.Parent);
        Assert.True(PatchApply.TryApply(project, versions, recovered).IsAccepted);
        Assert.True(Assert.IsType<Patch>(UnsettledRecovery.Create(deletion, project, versions, header, reservations)).IsEmpty);
    }

    private static async Task Keep(Replica replica, Patch patch)
    {
        await CollabHarness.Until(() => replica.Room.Reservations.Values.All(owner => owner == replica.Room.State.MyKey), "las reservas ajenas liberadas");
        await replica.Ui.Post(() =>
        {
            replica.Engine.Unsettled.Add(patch, "Este cambio no entró.");
            Assert.True(replica.Engine.KeepUnsettled(patch.Id));
        });
    }

    private static Task Converged(Replica a, Replica b, Replica c, long seq) => CollabHarness.Until(() =>
        a.Engine.CaptureForGuest().Seq == seq && a.Engine.CaptureForGuest() == b.Engine.CaptureForGuest()
        && a.Engine.CaptureForGuest() == c.Engine.CaptureForGuest()
        && a.HashOnUi() == b.HashOnUi() && a.HashOnUi() == c.HashOnUi(), "las tres copias recuperadas");

    [SalaFact]
    public async Task Conservar_borrado_en_tres_replicas_y_repetirlo_no_envia_nada()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await using Replica c = new();
        await JoinThird(a, c);
        Patch deletion = new(Guid.NewGuid().ToString("N"), 0, b.Room.State.MyKey, "Borrar clip",
            [new PatchOp(ElementKind.Clip, "c3", "v2", 0, null)], [], []);
        await a.Edit(p => p.Sequences[0].VideoTracks[1].Clips[0].Enabled = false);
        await Converged(a, b, c, 1);
        await Keep(b, deletion);
        await Converged(a, b, c, 2);
        Assert.Empty(b.Live.Sequences[0].VideoTracks[1].Clips);
        Assert.Empty(b.Engine.Unsettled.Items);
        int validations = 0;
        a.BeforeValidation(_ => { Interlocked.Increment(ref validations); return Task.CompletedTask; });
        await Keep(b, deletion);
        await b.Ui.Post(() => Assert.Empty(b.Engine.Unsettled.Items));
        Assert.Equal(0, Volatile.Read(ref validations));
        Assert.Equal(2, b.Engine.CaptureForGuest().Seq);
        Assert.DoesNotContain(new CollabJournal(b.Room.RoomFolder).Load(() => Assert.Fail("registro válido"))!.Entries,
            e => e.Kind is "pending" or "unsettled");
        Assert.Empty(a.Desyncs);
        Assert.Empty(b.Desyncs);
        Assert.Empty(c.Desyncs);
    }

    [SalaFact]
    public async Task Conservar_solo_orden_de_pistas_con_una_pista_y_un_clip_nuevos_intercalados()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await using Replica c = new();
        await JoinThird(a, c);
        Project reordered = b.Live.Clone();
        reordered.Sequences[0].VideoTracks.Reverse();
        Patch original = PatchDiff.Create(SharedState.Capture(b.Live), SharedState.Capture(reordered), new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 0, b.Room.State.MyKey, "Reordenar pistas"))!;
        Assert.NotNull(original);
        Assert.Empty(original.Ops);
        Assert.Single(original.Orders);
        await a.Edit(p =>
        {
            p.Sequences[0].VideoTracks.Insert(1, new Track { Id = "intercalada", Name = "Intercalada", Kind = TrackKind.Video });
            p.Sequences[0].VideoTracks[0].Clips.Insert(1, ModelSamples.MakeClip("intercalado", "m1", 10, 10, 11));
        });
        await Converged(a, b, c, 1);
        await Keep(b, original);
        await Converged(a, b, c, 2);
        Assert.Equal(new[] { "v2", "v1", "intercalada" }, b.Live.Sequences[0].VideoTracks.Select(x => x.Id));
        Assert.Equal(new[] { "c1", "intercalado", "c2" }, b.Live.Sequences[0].VideoTracks[1].Clips.Select(x => x.Id));
        Assert.Empty(b.Engine.Unsettled.Items);
        Assert.Empty(a.Desyncs);
        Assert.Empty(b.Desyncs);
        Assert.Empty(c.Desyncs);
    }

    [SalaFact]
    public async Task Conservar_altas_borrados_y_orden_en_un_solo_paso_en_tres_replicas()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await using Replica c = new();
        await JoinThird(a, c);
        await a.Edit(p => p.Sequences[0].Markers.AddRange(new[] { "primero", "borrar", "ultimo" }
            .Select(id => new Marker { Id = id, Name = id, Time = Time.FromSeconds(1) })));
        await Converged(a, b, c, 1);
        Project after = b.Live.Clone();
        after.Sequences[0].Markers.RemoveAll(m => m.Id == "borrar");
        after.Sequences[0].Markers.Reverse();
        after.Sequences[0].Markers.Insert(1, new Marker { Id = "alta", Name = "Nuevo recuperado", Time = Time.FromSeconds(2) });
        Patch original = PatchDiff.Create(SharedState.Capture(b.Live), SharedState.Capture(after), new ElementVersions(),
            new PatchHeader(Guid.NewGuid().ToString("N"), 1, b.Room.State.MyKey, "Paso mixto"))!;
        await a.Edit(p => p.Sequences[0].Markers.Insert(1, new Marker { Id = "intercalado", Name = "Intercalado", Time = Time.FromSeconds(3) }));
        await Converged(a, b, c, 2);
        await Keep(b, original);
        await Converged(a, b, c, 3);
        Marker added = Assert.Single(b.Live.Sequences[0].Markers, m => m.Name == "Nuevo recuperado");
        Assert.NotEqual("alta", added.Id);
        Assert.Equal(new[] { "ultimo", added.Id, "primero", "intercalado" }, b.Live.Sequences[0].Markers.Select(m => m.Id));
        Assert.Empty(b.Engine.Unsettled.Items);
        Assert.Empty(a.Desyncs);
        Assert.Empty(b.Desyncs);
        Assert.Empty(c.Desyncs);
    }

    [SalaFact]
    public async Task El_ganador_guarda_hasta_el_regreso_del_autor_y_acusa_duplicados()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        await using Replica c = new();
        await JoinThird(a, c);
        string author = c.Room.State.MyKey;
        string folder = c.Room.RoomFolder;
        a.Engine.OnConnectionLost();
        await c.Edit(p => p.Sequences[0].Markers.Add(new Marker { Id = "separado-ausente", Name = "Conservar", Time = Time.FromSeconds(2) }));
        await Converged(a, b, c, 1);
        c.Engine.Dispose();
        await c.Room.LeaveAsync();
        await CollabHarness.Until(() => a.Room.State.Members.Any(m => m.Key == author && !m.Online), "el autor está fuera");
        await a.Engine.HandleSupersededAsync();
        CollabJournal oldJournal = new(a.Room.RoomFolder);
        JournalEntry separated = Assert.Single(oldJournal.Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "separated");
        Assert.Equal(author, separated.Patch.Author);
        // Frontera del motor como en las otras pruebas de Superada: Sala hace el traspaso real.
        await a.Room.HandOverAsync(b.Room.State.MyKey);
        CollabJournal winner = new(b.Room.RoomFolder);
        await CollabHarness.Until(() => winner.Load(() => { })?.Entries.Any(e => e.Kind == "separated") == true, "la custodia en el ganador");
        await CollabHarness.Until(() => oldJournal.Load(() => { })?.Entries.All(e => e.Kind != "separated") == true, "el acuse de custodia");
        JsonElement ack = JsonSerializer.SerializeToElement(new { tipo = "cambio-separado-recibido", id = separated.Patch.Id });
        b.Engine.OnRecoveryDirect(a.Room.State.MyKey, ack);
        Assert.Single(winner.Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "separated");
        await b.Ui.Post(() =>
        {
            Project live = b.Live.Clone();
            b.Engine.Dispose();
            b.Attach(live);
            b.Engine.StartAsHost(1);
        });
        // La retirada tras el acuse falla una vez, como cuando el sondeo de esta prueba (o el antivirus) retiene
        // registro.jsonl en Windows: antes la entrega se reponía y el acuse no se repetía, y la prueba fallaba al azar.
        int failedSaves = 0;
        b.Engine.SaveJournal = s => (s.Entries.Any(e => e.Kind == "separated") || Interlocked.Exchange(ref failedSaves, 1) == 1) && winner.Save(s);
        Assert.Equal(author, Assert.Single(winner.Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "separated").Patch.Author);
        CollabInvitation invite = await b.Room.CreateInviteAsync(TimeSpan.FromMinutes(10));
        Task joining = c.Room.JoinAsync(invite.Link, "Eva", folder);
        await CollabHarness.Until(() => joining.IsCompleted || b.Room.State.Requests.Any(r => r.Key == author), "el regreso de la misma clave");
        if (!joining.IsCompleted) await b.Room.ApproveAsync(author, Rol.Editar);
        await joining.WaitAsync(TimeSpan.FromSeconds(20));
        ProjectParseResult snapshot = ProjectWire.TryParse(b.Engine.CaptureForGuest().Json);
        await c.Ui.Post(() =>
        {
            c.Attach(snapshot.Project!);
            c.Engine.StartAsGuest(snapshot.Project!.Clone(), snapshot.Versions!, 1);
        });
        await CollabHarness.Until(() => c.Engine.Unsettled.Items.Count == 1, "el autor guarda su cambio al volver");
        await CollabHarness.Until(() => winner.Load(() => { })?.Entries.All(e => e.Kind != "separated") == true, "el acuse del autor retira la entrega");
        Assert.Equal(1, Volatile.Read(ref failedSaves));
        JsonElement delivery = JsonSerializer.SerializeToElement(new { tipo = "cambio-separado", patch = JsonSerializer.Deserialize<JsonElement>(PatchWire.ToJson(separated.Patch)) });
        TaskCompletionSource duplicateAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Room.DirectReceived += (from, data) =>
        {
            if (from == author && data.TryGetProperty("tipo", out JsonElement type) && type.GetString() == "cambio-separado-recibido") duplicateAck.TrySetResult();
        };
        await b.Room.SendDirectAsync(delivery, author);
        await duplicateAck.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(separated.Patch.Id, Assert.Single(c.Engine.Unsettled.Items).Id);
        Assert.Single(new CollabJournal(folder).Load(() => Assert.Fail("registro válido"))!.Entries, e => e.Kind == "unsettled");
        Assert.Empty(a.Desyncs);
        Assert.Empty(b.Desyncs);
        Assert.Empty(c.Desyncs);
    }

    [SalaFact]
    public async Task La_cola_recorta_los_mas_antiguos_y_rechaza_mensajes_hostiles()
    {
        (Replica host, Replica guest) = await CollabEngineTests.StartAsync();
        await using Replica a = host;
        await using Replica b = guest;
        string author = new('f', 64);
        Patch sample = AddMarker(a.Live) with { Author = author };
        JournalEntry[] entries = [.. Enumerable.Range(0, CollabEngine.MaxSeparatedPerAuthor + 1)
            .Select(_ => new JournalEntry("separated", sample with { Id = Guid.NewGuid().ToString("N") }))];
        CollabJournal journal = new(a.Room.RoomFolder);
        await a.Ui.Post(() =>
        {
            a.Engine.Dispose();
            Assert.True(journal.Save(new JournalState(0, ProjectWire.ToJson(a.Live), null, [], entries)));
            a.Attach(a.Live.Clone());
            a.Engine.StartAsHost();
        });
        await CollabHarness.Until(() => journal.Load(() => { })?.Entries.Count(e => e.Kind == "separated") == CollabEngine.MaxSeparatedPerAuthor,
            "el recorte persistido tras restaurar");
        JournalState saved = journal.Load(() => Assert.Fail("registro válido"))!;
        Assert.Equal(CollabEngine.MaxSeparatedPerAuthor, saved.Entries.Count(e => e.Kind == "separated"));
        Assert.DoesNotContain(saved.Entries, e => e.Patch.Id == entries[0].Patch.Id);
        Assert.Contains(saved.Entries, e => e.Patch.Id == entries[^1].Patch.Id);
        await CollabHarness.Until(() => a.Notices.Any(n => n.Contains("más antiguos", StringComparison.Ordinal)), "el aviso del recorte");
        string before = File.ReadAllText(Path.Combine(a.Room.RoomFolder, "registro.jsonl"), Encoding.UTF8);
        foreach (string hostile in new[]
        {
            "null", "[]", "{\"tipo\":1}", "{\"tipo\":\"cambio-separado\",\"patch\":null}",
            "{\"tipo\":\"cambio-separado-recibido\",\"id\":42}",
            "{\"tipo\":\"cambio-separado-recibido\",\"id\":\"incorrecto\"}",
            "{\"tipo\":\"cambio-separado-recibido\",\"tipo\":\"cambio-separado-recibido\",\"id\":\"" + entries[^1].Patch.Id + "\"}",
            "{\"tipo\":\"cambio-separado\",\"patch\":\"" + new string('x', WireLimits.Default.MaxPatchBytes + 1) + "\"}"
        })
        {
            using JsonDocument message = JsonDocument.Parse(hostile);
            a.Engine.OnRecoveryDirect(author, message.RootElement);
            b.Engine.OnRecoveryDirect(a.Room.State.MyKey, message.RootElement);
        }
        Assert.Equal(before, File.ReadAllText(Path.Combine(a.Room.RoomFolder, "registro.jsonl"), Encoding.UTF8));
        Assert.Empty(b.Engine.Unsettled.Items);
    }
}
