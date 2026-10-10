using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.App.Player;

/// <summary>
/// Proyecto en memoria para el sondeo, igual que la demostración del visor: el primer video llena el cuadro con su
/// audio; los demás van encima como imagen en imagen al 35 % en una esquina, con el audio en silencio. Un
/// <c>.rodaje</c> se abre tal cual.
/// </summary>
internal sealed record DemoProject(Project Project, string SequenceId, string Directory)
{
    private const double PictureInPictureScale = 0.35;

    private static readonly GuidIdGenerator Ids = GuidIdGenerator.Instance;

    private static readonly (int X, int Y)[] Corners = [(1, -1), (1, 1), (-1, 1), (-1, -1)];

    public static DemoProject Open(IReadOnlyList<string> paths)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        string[] full = [.. paths.Select(Path.GetFullPath)];
        if (full.Length == 1 && string.Equals(Path.GetExtension(full[0]), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            Project loaded = ProjectSerializer.Load(full[0]);
            Sequence first = loaded.Sequences.FirstOrDefault()
                ?? throw new ProjectFormatException($"El proyecto «{Path.GetFileName(full[0])}» no tiene ninguna secuencia.");
            return new DemoProject(loaded, first.Id, Path.GetDirectoryName(full[0])!);
        }

        MediaInfo[] infos = [.. full.Select(MediaProbe.Probe)];
        string directory = Path.GetDirectoryName(full[0])!;
        MediaInfo? firstVideo = infos.FirstOrDefault(i => i.HasVideo);
        Project project = ProjectFactory.CreateDefault(Path.GetFileNameWithoutExtension(full[0]), Ids,
            firstVideo?.Video?.FrameRate ?? Rational.Fps30);
        Sequence sequence = project.Sequences[0];
        if (firstVideo?.Video is { } video)
        {
            sequence.Width = video.DisplayWidth;
            sequence.Height = video.DisplayHeight;
        }

        int videoTracks = 0;
        int audioTracks = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            MediaInfo info = infos[i];
            var media = new MediaRef { Id = Ids.NewId("media"), Path = Path.GetRelativePath(directory, full[i]) };
            info.ApplyTo(media);
            new AddMediaCommand(media).Apply(project);

            string? videoTrack = info.HasVideo ? TrackFor(project, sequence, TrackKind.Video, ++videoTracks) : null;
            string? audioTrack = info.HasAudio ? TrackFor(project, sequence, TrackKind.Audio, ++audioTracks) : null;
            if (audioTrack is not null && audioTracks > 1)
            {
                sequence.GetTrack(audioTrack).Muted = true;
            }

            if (videoTrack is null && audioTrack is null)
            {
                continue;
            }

            var place = Edits.Overwrite(sequence.Id, media.Id, Time.Zero, media.Duration, videoTrack ?? audioTrack!, Time.Zero,
                videoTrack is null ? null : audioTrack, Ids);
            place.Apply(project);
            if (videoTrack is not null && videoTracks > 1)
            {
                Clip clip = sequence.GetClip(place.CreatedIds[0]).Clip;
                clip.Effects.Add(PictureInPicture(sequence, media, videoTracks - 2));
            }
        }

        foreach (Track unused in sequence.VideoTracks.Skip(Math.Max(1, videoTracks)).Concat(sequence.AudioTracks.Skip(Math.Max(1, audioTracks))).ToList())
        {
            Edits.RemoveTrack(sequence.Id, unused.Id, Ids).Apply(project);
        }

        return new DemoProject(project, sequence.Id, directory);
    }

    private static string TrackFor(Project project, Sequence sequence, TrackKind kind, int number)
    {
        List<Track> tracks = kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks;
        if (number <= tracks.Count)
        {
            return tracks[number - 1].Id;
        }

        string name = (kind == TrackKind.Video ? "Video " : "Audio ") + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var add = Edits.AddTrack(sequence.Id, kind, tracks.Count, name, Ids);
        add.Apply(project);
        return add.CreatedIds[0];
    }

    private static EffectInstance PictureInPicture(Sequence sequence, MediaRef media, int corner)
    {
        double fit = Math.Min((double)sequence.Width / Math.Max(1, media.Width), (double)sequence.Height / Math.Max(1, media.Height));
        double width = media.Width * fit * PictureInPictureScale;
        double height = media.Height * fit * PictureInPictureScale;
        double margin = sequence.Height * 0.04;
        (int sx, int sy) = Corners[corner % Corners.Length];
        return new EffectInstance
        {
            Id = Ids.NewId("effect"),
            Type = BuiltInEffects.Transform,
            Parameters = new()
            {
                [BuiltInEffects.TransformParameters.X] = new Parameter { Value = Math.Round(sx * ((sequence.Width / 2.0) - (width / 2) - margin)) },
                [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = Math.Round(sy * ((sequence.Height / 2.0) - (height / 2) - margin)) },
                [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = PictureInPictureScale },
            },
        };
    }
}
