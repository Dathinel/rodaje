using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Running;

/// <summary>Proyectos de verdad (medios generados con el número de cuadro dibujado) para exportar de extremo a extremo.</summary>
internal static class ExportProjects
{
    public static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>
    /// 1080p30, 7 s: pista 1 con un clip con color, otro a 2× unido por un fundido de 0,5 s y otro invertido; pista 2 con un
    /// PiP transformado; audio AAC del primer medio en la pista de audio.
    /// </summary>
    public static Project TwoTracks(int width = 1920, int height = 1080)
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Exportar", FrameRate = Rational.Fps30, Width = width, Height = height };
        var project = new Project { Id = "p", Name = "Exportar", Sequences = [sequence] };
        MediaRef a = AddMedia(project, generator, TestMediaPresets.H264Ntsc24Aac, "a");
        MediaRef b = AddMedia(project, generator, TestMediaPresets.H264Ntsc30BFrames, "b");
        var main = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        var top = new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 2" };
        var sound = new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" };
        sequence.VideoTracks.AddRange([main, top]);
        sequence.AudioTracks.Add(sound);

        Clip color = Add(main, a.Id, "color", S(0), S(1), S(2.5));
        color.Effects.Add(Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.5), (BuiltInEffects.ColorParameters.Contrast, 0.2)));
        Clip fast = Add(main, b.Id, "rapido", S(2.5), S(2), S(2.5));
        fast.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        ClipTimeMap.Fit(fast.Start, S(2), S(7), fast.Speed).ApplyTo(fast);
        main.Transitions.Add(new TransitionInstance { Id = "t1", Type = BuiltInTransitions.Crossfade, From = "color", To = "rapido", Duration = S(0.5) });
        Clip reversed = Add(main, b.Id, "reversa", fast.End, S(9), S(2));
        reversed.Speed = new ClipSpeed { Rate = new Rational(1, 1), Reverse = true };
        ClipTimeMap.Fit(reversed.Start, S(9), S(11), reversed.Speed).ApplyTo(reversed);

        Clip pip = Add(top, a.Id, "pip", S(1), S(12), S(4));
        pip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.35), (BuiltInEffects.TransformParameters.X, -500)));
        Add(sound, a.Id, "sonido", S(0), S(1), reversed.End);

        foreach (Clip clip in sequence.VideoTracks.SelectMany(t => t.Clips))
        {
            clip.Effects.ForEach(e => e.Id = $"{clip.Id}-{e.Type}");
        }

        var issues = ModelValidator.Validate(project);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
        return project;
    }

    /// <summary>Una pista con <paramref name="seconds"/> s del medio 1080p a 29,97 repetido, y un PiP encima con color.</summary>
    public static Project Long(double seconds)
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Larga", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Larga", Sequences = [sequence] };
        MediaRef b = AddMedia(project, generator, TestMediaPresets.H264Ntsc30BFrames, "b");
        MediaRef h = AddMedia(project, generator, TestMediaPresets.Hevc25Mkv, "h");
        var main = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        var top = new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 2" };
        sequence.VideoTracks.AddRange([main, top]);
        int n = 0;
        for (double at = 0; at < seconds; at += 18)
        {
            double length = Math.Min(18, seconds - at);
            Add(main, b.Id, $"b{n}", S(at), S(1), S(length));
            Clip pip = Add(top, h.Id, $"h{n}", S(at), S(1), S(length));
            pip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.4), (BuiltInEffects.TransformParameters.X, 500)));
            pip.Effects.Add(Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.4)));
            pip.Effects.ForEach(e => e.Id = $"{pip.Id}-{e.Type}");
            n++;
        }

        return project;
    }

    private static MediaRef AddMedia(Project project, TestMediaGenerator generator, MediaSpec spec, string id)
    {
        var reference = new MediaRef { Id = id, Path = generator.GetOrCreate(spec).Path };
        MediaProbe.Probe(reference.Path).ApplyTo(reference);
        project.Media.Add(reference);
        return reference;
    }

    private static Clip Add(Track track, string mediaId, string id, Time start, Time sourceIn, Time duration)
    {
        var clip = new Clip { Id = id, MediaId = mediaId, Start = start, SourceIn = sourceIn, SourceOut = sourceIn + duration };
        track.Clips.Add(clip);
        track.SortClips();
        return clip;
    }

    private static EffectInstance Effect(string type, params (string Name, double Value)[] parameters)
    {
        var effect = new EffectInstance { Id = type, Type = type };
        foreach ((string name, double value) in parameters)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }
}
