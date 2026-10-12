using System.Globalization;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;

namespace Rodaje.App.Services.Audio;

/// <summary>
/// «Normalizar»: mide en segundo plano (EBU R128) y deja la ganancia en un paso del historial. Por clip sube o baja su
/// volumen (valor y keyframes); la mezcla entera cambia la ganancia maestra de la secuencia, sin pasar de −1 dBTP.
/// </summary>
internal static class LoudnessNormalizer
{
    public const double MaxTruePeakDbtp = -1;

    /// <summary>Los objetivos que se ofrecen, con su uso.</summary>
    public static IReadOnlyList<(double Lufs, string Name)> Targets { get; } =
    [
        (-14, "−14 LUFS · redes y streaming"),
        (-16, "−16 LUFS · podcast"),
        (-23, "−23 LUFS · televisión (EBU R128)"),
    ];

    /// <summary>«−14», «−23,5»: coma decimal y signo menos tipográfico.</summary>
    public static string Number(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−');

    /// <summary>Lee un objetivo escrito a mano («-18», «−18,5»); nulo si no es un número entre −60 y 0.</summary>
    public static double? ParseTarget(string? text)
    {
        string normalized = (text ?? "").Replace("LUFS", "", StringComparison.OrdinalIgnoreCase).Replace('−', '-').Replace(',', '.').Trim();
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value is >= -60 and <= 0
            ? value
            : null;
    }

    /// <summary>Los clips de audio que se normalizan: los seleccionados de pistas de audio, o el audio enlazado al video seleccionado.</summary>
    public static IReadOnlyList<string> AudioClips(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var ids = new List<string>();
        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is not { } found)
            {
                continue;
            }

            IEnumerable<(Track Track, Clip Clip)> group = found.Track.Kind == TrackKind.Audio ? [found] : session.Sequence.GetLinkGroup(id);
            foreach ((Track track, Clip clip) in group)
            {
                if (track.Kind == TrackKind.Audio && !track.Locked && clip.Generator is null && !ids.Contains(clip.Id))
                {
                    ids.Add(clip.Id);
                }
            }
        }

        return ids;
    }

    /// <summary>Mide cada clip solo y sube o baja su volumen hasta <paramref name="targetLufs"/>, todos en un paso.</summary>
    public static async Task<bool> NormalizeClipsAsync(EditorSession session, IReadOnlyList<string> clipIds, double targetLufs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clipIds);
        if (clipIds.Count == 0)
        {
            return false;
        }

        Project copy = session.Project.Clone();
        string sequenceId = session.SequenceId;
        string? folder = ProjectFolder(session);
        var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
        int silent = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (string id in clipIds)
                {
                    LoudnessMeasurement measured = OfflineAudio.MeasureClip(copy, sequenceId, id, folder, CancellationToken.None);
                    if (!double.IsFinite(measured.IntegratedLufs))
                    {
                        silent++;
                        continue;
                    }

                    double delta = Math.Round(measured.GainTo(targetLufs, double.PositiveInfinity), 2);
                    if (Math.Abs(delta) >= 0.01)
                    {
                        deltas[id] = delta;
                    }
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Error, "No se pudo normalizar", ex.Message);
            return false;
        }

        string target = Number(targetLufs) + " LUFS";
        if (deltas.Count == 0)
        {
            session.Notify(NotificationSeverity.Informational, "Normalizar", silent == clipIds.Count
                ? "El clip está en silencio: no hay nada que normalizar."
                : $"Ya estaba a {target}.");
            return false;
        }

        string plural = deltas.Count == 1 ? "" : $" ({deltas.Count} clips)";
        return session.Execute(Edits.ShiftGain(sequenceId, deltas, $"Normalizar a {target}{plural}"));
    }

    /// <summary>Mide la mezcla de toda la secuencia y fija la ganancia maestra para llegar a <paramref name="targetLufs"/>.</summary>
    public static async Task<bool> NormalizeMixAsync(EditorSession session, double targetLufs)
    {
        ArgumentNullException.ThrowIfNull(session);
        Project copy = session.Project.Clone();
        string sequenceId = session.SequenceId;
        string? folder = ProjectFolder(session);
        double current = session.Sequence.MasterGainDb;
        LoudnessMeasurement measured;
        try
        {
            measured = await Task.Run(() =>
            {
                using OfflineAudio audio = OfflineAudio.Open(copy, sequenceId, folder);
                return audio.Duration > Time.Zero
                    ? audio.MeasureLoudness(Time.Zero, audio.Duration, null, CancellationToken.None)
                    : new LoudnessMeasurement(double.NegativeInfinity, double.NegativeInfinity, 0);
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException
            or Rodaje.Media.Export.ExportException)
        {
            session.Notify(NotificationSeverity.Error, "No se pudo medir la mezcla", ex.Message);
            return false;
        }

        if (!double.IsFinite(measured.IntegratedLufs))
        {
            session.Notify(NotificationSeverity.Informational, "Normalizar la mezcla", "La secuencia está en silencio: no hay nada que normalizar.");
            return false;
        }

        double gain = Math.Round(current + measured.GainTo(targetLufs, MaxTruePeakDbtp), 2);
        string note = measured.GainTo(targetLufs, MaxTruePeakDbtp) < measured.GainTo(targetLufs, double.PositiveInfinity) - 0.05
            ? $" Se quedó corta para no pasar de {Number(MaxTruePeakDbtp)} dBTP."
            : "";
        bool done = session.Execute(Edits.SetMasterGain(sequenceId, gain, $"Normalizar la mezcla a {Number(targetLufs)} LUFS"));
        if (done)
        {
            session.Notify(NotificationSeverity.Success, "Mezcla normalizada",
                $"Medía {Number(measured.IntegratedLufs)} LUFS; ganancia maestra {Signed(gain)} dB.{note}");
        }

        return done;
    }

    public static string Signed(double db) => (db > 0 ? "+" : "") + Number(db);

    private static string? ProjectFolder(EditorSession session) => session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
}
