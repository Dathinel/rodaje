using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    private static async Task TranscriptAsync(MainWindow window)
    {
        var watch = Stopwatch.StartNew();
        EditorSession session = window.Session;
        Clip clip = session.MainTrack?.Clips.FirstOrDefault()
            ?? throw new InvalidOperationException("La autoprueba de transcripción necesita el clip de prueba.");
        MediaRef media = session.Project.GetMedia(clip.MediaId);
        int count = (int)Math.Floor(media.Duration.ToSeconds() / 0.4);
        if (count < 3) throw new InvalidOperationException("El clip de prueba necesita al menos tres palabras.");
        var transcript = new MediaTranscript
        {
            Id = "autoprueba-transcript", MediaId = media.Id, Language = "es", Model = "sintético",
            Created = DateTime.UtcNow, MediaDuration = media.Duration,
            Words = [.. Enumerable.Range(0, count).Select(i => new SpokenWord
            {
                Start = Time.FromSeconds(i * 0.4), End = Time.FromSeconds(i * 0.4 + 0.25),
                Text = "palabra" + i.ToString(CultureInfo.InvariantCulture), Confidence = 1,
            })],
        };
        if (!session.Execute(new SetTranscriptCommand(media.Id, transcript)))
            throw new InvalidOperationException("No se pudo guardar la transcripción sintética.");

        window.LibraryPane.ShowTranscript();
        Views.Transcript.TranscriptPanel panel = window.LibraryPane.TranscriptPanel;
        for (int i = 0; i < 40 && !panel.HasVisibleWords; i++) await Task.Delay(25);
        if (!panel.HasVisibleWords || panel.Model.Document.Words.Count < 3)
            throw new InvalidOperationException("El panel de transcripción no muestra las palabras sintéticas.");

        DocWord[] before = [.. panel.Model.Document.Words];
        Time duration = session.Sequence.Duration;
        panel.Model.SelectWord(1);
        session.SelectTranscriptWords(panel.Model);
        if (!session.SelectedClipIds.Contains(before[1].ClipId))
            throw new InvalidOperationException("Seleccionar texto no seleccionó su clip.");
        if (!session.ExecuteTranscript(panel.Model.DeleteSelection(true)) || session.Sequence.Duration >= duration
            || !panel.Model.Document.Words.Select(w => w.Text).SequenceEqual(before.Where((_, i) => i != 1).Select(w => w.Text)))
            throw new InvalidOperationException("Borrar texto no quitó exactamente una palabra con ripple.");

        panel.ShowRemovedWords(true);
        panel.UpdateLayout();
        if (!panel.HasVisibleRemovedWords)
            throw new InvalidOperationException("Mostrar lo quitado no dibujó la palabra descartada.");
        RemovedTranscriptSpan removed = panel.Model.RemovedWords.Single();
        if (!session.ExecuteTranscript(panel.Model.Recover(removed)) || session.Sequence.Duration != duration
            || !panel.Model.Document.Words.Select(w => w.Text).SequenceEqual(before.Select(w => w.Text)))
            throw new InvalidOperationException("Recuperar no restauró el texto y la duración.");
        session.Undo();
        panel.ShowRemovedWords(false);
        session.Undo();
        if (session.Sequence.Duration != duration || !panel.Model.Document.Words.SequenceEqual(before))
            throw new InvalidOperationException("Deshacer no restauró la transcripción y la duración.");

        if (!session.ExecuteTranscript(panel.Model.AddSpeaker("Ana", "#123456")))
            throw new InvalidOperationException("No se pudo añadir un hablante.");
        panel.Model.SelectWord(1);
        if (!session.ExecuteTranscript(panel.Model.AssignSpeaker(1)))
            throw new InvalidOperationException("No se pudo asignar el hablante.");
        panel.UpdateLayout();
        if (!panel.HasVisibleSpeakerNames || !panel.Model.Document.Paragraphs.Any(p => panel.Model.SpeakerOf(p) is { Name: "Ana", Color: "#123456" }))
            throw new InvalidOperationException("El panel no mostró el hablante y su color.");
        panel.Model.SelectWord(1);
        if (!session.ExecuteTranscript(panel.Model.CorrectWord("palabra corregida"))
            || !panel.Model.Document.Words.Skip(1).Take(2).Select(w => w.Text).SequenceEqual(["palabra", "corregida"]))
            throw new InvalidOperationException("Corregir no partió la palabra en el panel.");
        session.Undo();
        session.Undo();
        session.Undo();
        if (!panel.Model.Document.Words.SequenceEqual(before))
            throw new InvalidOperationException("Deshacer no restauró las palabras y sus hablantes.");
        session.Undo();
        window.LibraryPane.ShowMarkers();
        double stepMs = watch.Elapsed.TotalMilliseconds;
        string opening = await MeasureTranscriptHourAsync(window, media);
        string result = string.Create(CultureInfo.InvariantCulture, $"transcripcion: OK; {before.Length} palabras; paso {stepMs:0.0} ms; {opening}");
        Program.Metrics.Note(result);
        File.WriteAllText(Path.Combine(AppDataPaths.Root, "autoprueba-transcripcion.txt"), result);
    }

    private static async Task<string> MeasureTranscriptHourAsync(MainWindow window, MediaRef original)
    {
        EditorSession session = window.Session;
        int steps = session.History.Count;
        MediaRef media = original.Clone();
        media.Id = "autoprueba-hora-media";
        media.Duration = Time.FromSeconds(3600);
        if (!session.Execute(new AddMediaCommand(media)))
            throw new InvalidOperationException("No se pudo preparar el medio sintético de una hora.");
        string track = session.Sequence.AudioTracks.FirstOrDefault()?.Id ?? session.Sequence.VideoTracks[0].Id;
        if (!session.Execute(Edits.InsertMedia(session.SequenceId, media.Id, Time.Zero, media.Duration, track, session.Sequence.Duration)))
            throw new InvalidOperationException("No se pudo preparar el clip sintético de una hora.");
        var transcript = new MediaTranscript
        {
            Id = "autoprueba-hora-transcript", MediaId = media.Id, Language = "es", Model = "sintético",
            Created = DateTime.UtcNow, MediaDuration = media.Duration,
            Words = [.. Enumerable.Range(0, 9000).Select(i => new SpokenWord
            {
                Start = Time.FromSeconds(i * 0.4), End = Time.FromSeconds(i * 0.4 + 0.25),
                Text = "palabra" + i.ToString(CultureInfo.InvariantCulture), Confidence = 1,
            })],
        };
        if (!session.Execute(new SetTranscriptCommand(media.Id, transcript)))
            throw new InvalidOperationException("No se pudo preparar la transcripción de una hora.");

        var watch = Stopwatch.StartNew();
        window.LibraryPane.ShowTranscript();
        Views.Transcript.TranscriptPanel panel = window.LibraryPane.TranscriptPanel;
        panel.UpdateLayout();
        for (int i = 0; i < 40 && !panel.HasVisibleWords; i++) await Task.Delay(5);
        double elapsed = watch.Elapsed.TotalMilliseconds;
        if (!panel.HasVisibleWords || panel.Model.Document.Words.Count != 9000 || panel.RealizedWordCount >= 9000)
            throw new InvalidOperationException("El panel de una hora no muestra párrafos virtualizados.");
        int realized = panel.RealizedWordCount;
        window.LibraryPane.ShowMarkers();
        while (session.History.Count > steps) session.Undo();
        return string.Create(CultureInfo.InvariantCulture, $"apertura_1h {elapsed:0.0} ms; 9000 palabras; {realized} realizadas");
    }
}
