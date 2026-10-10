using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.Visor;

/// <summary>Lo que reproduce el visor: un proyecto, la secuencia que se ve, su historial y dónde se guarda.</summary>
internal sealed class VisorProject
{
    public const double PictureInPictureScale = 0.35;

    private static readonly GuidIdGenerator Ids = GuidIdGenerator.Instance;

    private static readonly (int X, int Y)[] Corners = [(1, -1), (1, 1), (-1, 1), (-1, -1)];

    private VisorProject(Project project, string sequenceId, string directory, string savePath, bool isDemo)
    {
        Project = project;
        SequenceId = sequenceId;
        Directory = directory;
        SavePath = savePath;
        IsDemo = isDemo;
        History = new UndoStack(project);
    }

    public Project Project { get; }

    public string SequenceId { get; }

    public Sequence Sequence => Project.GetSequence(SequenceId);

    /// <summary>Carpeta contra la que se resuelven las rutas relativas de los medios.</summary>
    public string Directory { get; }

    public string SavePath { get; }

    /// <summary>Armado en memoria a partir de videos sueltos (no se abrió un <c>.rodaje</c>).</summary>
    public bool IsDemo { get; }

    public UndoStack History { get; }

    public static bool IsProjectFile(string path) =>
        string.Equals(Path.GetExtension(path), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Abre un proyecto o arma la demostración con los videos.</summary>
    /// <exception cref="ArgumentException">Se mezcló un proyecto con otros archivos.</exception>
    public static VisorProject Open(IReadOnlyList<string> paths)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        string[] full = [.. paths.Select(Path.GetFullPath)];
        if (full.Any(IsProjectFile))
        {
            if (full.Length > 1)
            {
                throw new ArgumentException("Un proyecto .rodaje se abre solo; no se puede mezclar con videos sueltos.");
            }

            return Load(full[0]);
        }

        return BuildDemo(full);
    }

    public static VisorProject Load(string path)
    {
        Project project = ProjectSerializer.Load(path);
        Sequence sequence = project.Sequences.FirstOrDefault()
            ?? throw new ProjectFormatException($"El proyecto «{Path.GetFileName(path)}» no tiene ninguna secuencia.");
        return new VisorProject(project, sequence.Id, Path.GetDirectoryName(path)!, path, isDemo: false);
    }

    /// <summary>
    /// El primer video llena el cuadro en la pista de video 1 con su audio enlazado en la de audio 1; cada video
    /// siguiente va en su propia pista, encima, como imagen en imagen en una esquina, y su audio en una pista muda.
    /// </summary>
    public static VisorProject BuildDemo(IReadOnlyList<string> videoPaths)
    {
        MediaInfo[] infos = [.. videoPaths.Select(MediaProbe.Probe)];
        string directory = Path.GetDirectoryName(videoPaths[0])!;
        MediaInfo? first = infos.FirstOrDefault(i => i.HasVideo);
        Project project = ProjectFactory.CreateDefault(Path.GetFileNameWithoutExtension(videoPaths[0]), Ids,
            first?.Video?.FrameRate ?? Rational.Fps30);
        Sequence sequence = project.Sequences[0];
        if (first?.Video is { } video)
        {
            sequence.Width = video.DisplayWidth;
            sequence.Height = video.DisplayHeight;
        }

        int videoTracks = 0;
        int audioTracks = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            MediaInfo info = infos[i];
            var media = new MediaRef { Id = Ids.NewId("media"), Path = Path.GetRelativePath(directory, videoPaths[i]) };
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

        RemoveUnused(project, sequence.VideoTracks.Skip(Math.Max(1, videoTracks)));
        RemoveUnused(project, sequence.AudioTracks.Skip(Math.Max(1, audioTracks)));
        string savePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(videoPaths[0]) + ProjectSerializer.FileExtension);
        return new VisorProject(project, sequence.Id, directory, savePath, isDemo: true);
    }

    public void Save()
    {
        ProjectSerializer.Save(Project, SavePath);
        History.MarkClean();
    }

    /// <summary>Guarda una copia en otra ruta, con las rutas de los medios absolutas para que siga abriendo desde ahí.</summary>
    /// <returns>El proyecto tal como quedó guardado.</returns>
    public Project SaveCopy(string path)
    {
        Project copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(Project));
        foreach (MediaRef media in copy.Media)
        {
            media.Path = media.ResolvePath(Directory);
        }

        ProjectSerializer.Save(copy, path);
        return copy;
    }

    /// <summary>La pista número <paramref name="number"/> (desde 1) de su tipo; la crea si todavía no existe.</summary>
    private static string TrackFor(Project project, Sequence sequence, TrackKind kind, int number)
    {
        List<Track> tracks = kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks;
        if (number <= tracks.Count)
        {
            return tracks[number - 1].Id;
        }

        int index = tracks.Count;
        string name = (kind == TrackKind.Video ? "Video " : "Audio ") + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var add = Edits.AddTrack(sequence.Id, kind, index, name, Ids);
        add.Apply(project);
        return add.CreatedIds[0];
    }

    private static void RemoveUnused(Project project, IEnumerable<Track> tracks)
    {
        foreach (Track track in tracks.ToList())
        {
            Edits.RemoveTrack(project.Sequences[0].Id, track.Id, Ids).Apply(project);
        }
    }

    /// <summary>Transformación de imagen en imagen en la esquina <paramref name="corner"/> (arriba a la derecha, abajo a la derecha, abajo a la izquierda, arriba a la izquierda).</summary>
    private static EffectInstance PictureInPicture(Sequence sequence, MediaRef media, int corner)
    {
        double fit = Math.Min((double)sequence.Width / Math.Max(1, media.Width), (double)sequence.Height / Math.Max(1, media.Height));
        double width = media.Width * fit * PictureInPictureScale;
        double height = media.Height * fit * PictureInPictureScale;
        double margin = sequence.Height * 0.04;
        (int sx, int sy) = Corners[corner % Corners.Length];
        double x = sx * ((sequence.Width / 2.0) - (width / 2) - margin);
        double y = sy * ((sequence.Height / 2.0) - (height / 2) - margin);
        return new EffectInstance
        {
            Id = Ids.NewId("effect"),
            Type = BuiltInEffects.Transform,
            Parameters = new()
            {
                [BuiltInEffects.TransformParameters.X] = new Parameter { Value = Math.Round(x) },
                [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = Math.Round(y) },
                [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = PictureInPictureScale },
            },
        };
    }
}
