using Rodaje.Audio.Processing;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.Engine.Timeline;

internal sealed partial class TimelinePlan
{
    /// <summary>
    /// Un compuesto de audio se mezcla aparte si tiene volumen, fundidos, ecualizador o compresor, una transición, o si
    /// alguna pista de su secuencia lleva volumen o efectos; si no, se despliega en sus clips.
    /// </summary>
    private static bool AudioGroup(Project project, Track track, Clip compound)
    {
        EffectInstance? audio = compound.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        return GainCurve(compound.Effects, Time.Zero) is not null || AudioEffects(compound.Effects) is not null
            || Samples(audio, BuiltInEffects.AudioParameters.FadeIn) > 0 || Samples(audio, BuiltInEffects.AudioParameters.FadeOut) > 0
            || TouchesTransition(track, compound)
            || (project.FindSequence(compound.SequenceId!) is { } nested
                && nested.AudioTracks.Exists(t => !t.Muted && (GainCurve(t.Effects, Time.Zero) is not null || AudioEffects(t.Effects) is not null)));
    }

    /// <summary>Arma las pistas de audio; un compuesto que se mezcla aparte lleva las de su secuencia en su <see cref="AudioClipPlan.Bus"/>.</summary>
    private sealed class AudioBuilder(Project project, Dictionary<string, MediaRef> media, string projectDirectory, Func<Track, Clip, bool> keep)
    {
        private readonly Dictionary<Clip, (Time Before, Time After)> _extend = [];

        /// <param name="offset">
        /// Dónde empieza en la línea de tiempo el tiempo de la pista (el de sus keyframes): 0 en la secuencia, el del inicio de
        /// la secuencia anidada en las de un compuesto.
        /// </param>
        public AudioTrackPlan Track(Track track, Time offset, int depth)
        {
            List<Clip> included = [.. Ordered(track).Where(c => c.Enabled && c.Duration > Time.Zero && (c.IsCompound
                ? depth < ModelValidator.MaxCompoundDepth && project.FindSequence(c.SequenceId!) is not null
                : c.Generator is null && media.TryGetValue(c.MediaId, out MediaRef? m) && m.HasAudio))];
            List<Cut> cuts = Cuts(track, included);
            Curve? trackDb = GainCurve(track.Effects, offset);
            var clips = new List<AudioClipPlan>();
            for (int c = 0; c < included.Count; c++)
            {
                Clip clip = included[c];
                Cut? entering = cuts.Find(x => x.To == c);
                Cut? leaving = cuts.Find(x => x.From == c);
                (Time extendBefore, Time extendAfter) = _extend.GetValueOrDefault(clip);
                Time before = (entering?.Before ?? Time.Zero) + extendBefore;
                Time after = (leaving?.After ?? Time.Zero) + extendAfter;
                long clipStart = clip.Start.ToSamples(AudioSampleRate);
                long start = (clip.Start - before).ToSamples(AudioSampleRate);
                long end = (clip.End + after).ToSamples(AudioSampleRate);
                long sourceIn = clip.SourceIn.ToSamples(AudioSampleRate) - (clipStart - start);
                if (end <= start)
                {
                    continue;
                }

                if (clip.IsCompound)
                {
                    clips.Add(new AudioClipPlan(clip.Id, default, start, end, sourceIn, Gain(clip, trackDb, entering, leaving), null,
                        AudioEffects(clip.Effects))
                    {
                        Bus = Bus(clip, clip.Start - before, clip.End + after, depth),
                        Automation = Automation(clip.Effects, Time.Zero),
                    });
                    continue;
                }

                clips.Add(new AudioClipPlan(clip.Id, Key(media[clip.MediaId], projectDirectory), start, end, sourceIn,
                    Gain(clip, trackDb, entering, leaving), Retime(clip), AudioEffects(clip.Effects)) { Automation = Automation(clip.Effects, Time.Zero) });
            }

            return new AudioTrackPlan(track.Id, JoinSplitPieces(clips, included, cuts), AudioEffects(track.Effects))
            {
                Automation = Automation(track.Effects, offset),
            };
        }

        /// <summary>Las pistas de audio de la secuencia del compuesto que suenan en <c>[from, to)</c>, llevadas a la línea de tiempo.</summary>
        private AudioBusPlan Bus(Clip compound, Time from, Time to, int depth)
        {
            Sequence nested = project.FindSequence(compound.SequenceId!)!;
            Sequence flat = CompoundOps.Flatten(project, nested, keep, [nested.Id]);
            Time offset = compound.Start - compound.SourceIn;
            var window = new Clip { Start = from, SourceIn = from - offset, SourceOut = to - offset };
            var tracks = new List<AudioTrackPlan>();
            foreach (Track inner in flat.AudioTracks)
            {
                if (inner.Muted)
                {
                    continue;
                }

                var windowed = new Track
                {
                    Id = compound.Id + CompoundOps.IdSeparator + inner.Id,
                    Kind = TrackKind.Audio,
                    Effects = inner.Effects,
                };
                ExtendEdges(CompoundOps.Window(inner, window, windowed, id => compound.Id + CompoundOps.IdSeparator + id), from, to,
                    compound.Start, compound.End, _extend);
                windowed.SortClips();
                tracks.Add(Track(windowed, offset, depth + 1));
            }

            return new AudioBusPlan(AssignKeys(tracks));
        }
    }

    /// <summary>
    /// El clip de audio solo, para medirlo: su tramo propio en la línea de tiempo, su volumen y su velocidad, sin la pista,
    /// fundidos ni transiciones (aunque la pista esté silenciada). Nulo si no es un clip con sonido.
    /// </summary>
    internal static AudioClipPlan? Isolated(Project project, string sequenceId, string clipId, string projectDirectory)
    {
        if (project.GetSequence(sequenceId).FindClip(clipId) is not { } found || found.Track.Kind != TrackKind.Audio
            || found.Clip is not { Generator: null } clip || clip.Duration <= Time.Zero
            || project.Media.Find(m => m.Id == clip.MediaId) is not { HasAudio: true } media)
        {
            return null;
        }

        Curve? db = GainCurve(clip.Effects, Time.Zero);
        return new AudioClipPlan(clip.Id, Key(media, projectDirectory), clip.Start.ToSamples(AudioSampleRate), clip.End.ToSamples(AudioSampleRate),
            clip.SourceIn.ToSamples(AudioSampleRate), db is null ? null : new GainPlan(db, null, default, default, default, default), Retime(clip),
            AudioEffects(clip.Effects));
    }

    /// <summary>
    /// Los trozos seguidos de un clip dividido (sin transición entre medias, mismo medio y mismo tono) comparten el mapeo
    /// de todos juntos, así el lector pasa de uno a otro sin rehacer nada y suena igual que el clip entero.
    /// </summary>
    private static List<AudioClipPlan> JoinSplitPieces(List<AudioClipPlan> clips, List<Clip> included, List<Cut> cuts)
    {
        int start = 0;
        while (start < clips.Count)
        {
            int end = start + 1;
            ClipTimeMap? course = clips[start].Retime?.Map;
            while (course is not null && end < clips.Count && clips[end].Retime is { } next
                && clips[end].Source == clips[start].Source && next.PreservePitch == clips[start].Retime!.PreservePitch
                && !cuts.Exists(c => included[c.From].Id == clips[end - 1].ClipId)
                && course.Join(next.Map) is { } joined)
            {
                course = joined;
                end++;
            }

            for (int i = start; end - start > 1 && i < end; i++)
            {
                clips[i] = clips[i] with { Retime = clips[i].Retime! with { Joined = course } };
            }

            start = end;
        }

        return clips;
    }

    /// <summary>
    /// El primer ecualizador y el primer compresor activos (en ese orden, sea cual sea el de la pila); nulo si no hay o si
    /// el ecualizador no cambia nada y no hay compresor. Los parámetros no se animan: vale su valor fijo.
    /// </summary>
    internal static AudioEffectsSettings? AudioEffects(IReadOnlyList<EffectInstance> effects)
    {
        EffectInstance? eq = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Equalizer);
        EffectInstance? compressor = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Compressor);
        EqualizerSettings? equalizer = null;
        if (eq is not null)
        {
            equalizer = new EqualizerSettings(
                Value(eq, BuiltInEffects.EqualizerParameters.LowCut),
                Value(eq, BuiltInEffects.EqualizerParameters.LowGain),
                Value(eq, BuiltInEffects.EqualizerParameters.LowFrequency),
                Value(eq, BuiltInEffects.EqualizerParameters.MidGain),
                Value(eq, BuiltInEffects.EqualizerParameters.MidFrequency),
                Value(eq, BuiltInEffects.EqualizerParameters.MidQ),
                Value(eq, BuiltInEffects.EqualizerParameters.PresenceGain),
                Value(eq, BuiltInEffects.EqualizerParameters.PresenceFrequency),
                Value(eq, BuiltInEffects.EqualizerParameters.PresenceQ),
                Value(eq, BuiltInEffects.EqualizerParameters.HighGain),
                Value(eq, BuiltInEffects.EqualizerParameters.HighFrequency));
            equalizer = equalizer.IsIdentity ? null : equalizer;
        }

        CompressorSettings? dynamics = compressor is null ? null : Dynamics(compressor);
        string? sidechain = compressor?.Options.GetValueOrDefault(BuiltInEffects.CompressorOptions.Sidechain);
        return equalizer is null && dynamics is null
            ? null
            : new AudioEffectsSettings(equalizer, dynamics) { SidechainTrackId = string.IsNullOrEmpty(sidechain) ? null : sidechain };
    }

    private static CompressorSettings Dynamics(EffectInstance effect)
    {
        double V(string name) => Value(effect, name);
        bool On(string name) => Value(effect, name) >= 0.5;
        MultibandSettings? bands = V(K.Bands) >= 2
            ? new MultibandSettings(V(K.LowCrossover), V(K.HighCrossover), V(K.LowThreshold), V(K.MidThreshold), V(K.HighThreshold),
                V(K.LowGain), V(K.MidGain), V(K.HighGain))
            : null;
        return new CompressorSettings(V(K.Threshold), V(K.Ratio), V(K.Attack), V(K.Release), V(K.Makeup), V(K.Knee))
        {
            Detector = On(K.Detector) ? LevelDetector.Rms : LevelDetector.Peak,
            RmsWindowMs = V(K.RmsWindow),
            AutoRelease = On(K.AutoRelease),
            AutoMakeup = On(K.AutoMakeup),
            LookaheadMs = V(K.Lookahead),
            Linked = On(K.StereoLink),
            Mix = V(K.Mix) / 100,
            SidechainHighPassHz = V(K.SidechainHighPass),
            Multiband = bands,
            CeilingDb = On(K.Limiter) ? V(K.Ceiling) : null,
        };
    }

    /// <summary>
    /// Umbral, relación y mezcla animados del primer compresor activo; nulo si ninguno tiene keyframes. En un clip las
    /// posiciones son muestras del origen; en una pista o la mezcla, de la línea de tiempo desde <paramref name="offset"/>.
    /// </summary>
    internal static DynamicsAutomation? Automation(IReadOnlyList<EffectInstance> effects, Time offset)
    {
        EffectInstance? compressor = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Compressor);
        if (compressor is null || !compressor.Parameters.Any(p => p.Key is K.Threshold or K.Ratio or K.Mix && p.Value.Keyframes.Count > 0))
        {
            return null;
        }

        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Compressor)!;
        Curve Of(string name)
        {
            ParameterDefinition p = definition.FindParameter(name)!;
            return Curve.From(compressor.Parameters.GetValueOrDefault(name), p.DefaultValue, p.Clamp,
                ticks => Time.FromTicks(ticks + offset.Ticks).ToSamples(AudioSampleRate));
        }

        return new DynamicsAutomation(Of(K.Threshold), Of(K.Ratio), Of(K.Mix));
    }

    /// <summary>
    /// Marca las pistas que son sidechain de otro compresor del mismo nivel con lo que deben ir por delante, y quita las
    /// llaves que no valen: a sí misma, a una pista que no está (silenciada o de otro nivel) o desde una pista que ya es llave.
    /// </summary>
    internal static List<AudioTrackPlan> AssignKeys(List<AudioTrackPlan> tracks)
    {
        var ids = new HashSet<string>(tracks.Select(t => t.TrackId), StringComparer.Ordinal);
        var ahead = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (AudioTrackPlan track in tracks)
        {
            int latency = track.Effects?.LatencyFrames(AudioSampleRate) ?? 0;
            Note(track.Effects, track.TrackId, latency);
            foreach (AudioClipPlan clip in track.Clips)
            {
                Note(clip.Effects, track.TrackId, latency + (clip.Effects?.LatencyFrames(AudioSampleRate) ?? 0));
            }
        }

        if (ahead.Count == 0 && !tracks.Exists(t => t.Effects?.SidechainTrackId is not null || t.Clips.Any(c => c.Effects?.SidechainTrackId is not null)))
        {
            return tracks;
        }

        return tracks.ConvertAll(track =>
        {
            bool isKey = ahead.TryGetValue(track.TrackId, out int frames);
            return track with
            {
                KeyAhead = isKey ? frames : -1,
                Effects = Valid(track.Effects, track.TrackId, isKey),
                Clips = track.Clips.Any(c => c.Effects?.SidechainTrackId is not null)
                    ? [.. track.Clips.Select(c => c with { Effects = Valid(c.Effects, track.TrackId, isKey) })]
                    : track.Clips,
            };
        });

        void Note(AudioEffectsSettings? effects, string owner, int frames)
        {
            if (effects?.SidechainTrackId is { } key && key != owner && ids.Contains(key) && effects.Compressor is not null)
            {
                ahead[key] = Math.Max(frames, ahead.GetValueOrDefault(key));
            }
        }

        AudioEffectsSettings? Valid(AudioEffectsSettings? effects, string owner, bool ownerIsKey) =>
            effects?.SidechainTrackId is { } key && (ownerIsKey || key == owner || !ahead.ContainsKey(key))
                ? effects with { SidechainTrackId = null }
                : effects;
    }
    private static double Value(EffectInstance effect, string name)
    {
        ParameterDefinition definition = EffectRegistry.Find(effect.Type)!.FindParameter(name)!;
        return effect.Parameters.TryGetValue(name, out Parameter? p) ? definition.Clamp(p.Keyframes.Count > 0 ? p.Keyframes[0].Value : p.Value) : definition.DefaultValue;
    }

    /// <summary>Velocidad del clip; nula sin ella o si deja el origen tal cual (1× sin reversa ni congelado).</summary>
    private static AudioRetime? Retime(Clip clip)
    {
        if (clip.Speed is not { } speed)
        {
            return null;
        }

        ClipTimeMap map = clip.TimeMap;
        return map.IsIdentity ? null : new AudioRetime(map, speed.PreservePitch);
    }

    /// <summary>Volumen del clip: su <c>rodaje.audio</c>, la ganancia de la pista y los fundidos; nulo si suena tal cual.</summary>
    private static GainPlan? Gain(Clip clip, Curve? trackDb, Cut? entering, Cut? leaving)
    {
        EffectInstance? audio = clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        long clipStart = clip.Start.ToSamples(AudioSampleRate);
        long clipEnd = clip.End.ToSamples(AudioSampleRate);
        long length = clipEnd - clipStart;

        // Una transición reemplaza el fundido propio de ese lado: con los dos, el clip callaría antes de cruzarse.
        Fade fadeIn = default;
        Fade fadeOut = default;
        Fade crossIn = default;
        Fade crossOut = default;
        if (entering is { } e)
        {
            crossIn = Cross(e, fadeOut: false);
        }
        else
        {
            fadeIn = new Fade(clipStart, Math.Min(length, Samples(audio, BuiltInEffects.AudioParameters.FadeIn)), Out: false);
        }

        if (leaving is { } l)
        {
            crossOut = Cross(l, fadeOut: true);
        }
        else
        {
            long fade = Math.Min(length, Samples(audio, BuiltInEffects.AudioParameters.FadeOut));
            fadeOut = new Fade(clipEnd - fade, fade, Out: true);
        }

        var gain = new GainPlan(GainCurve(clip.Effects, Time.Zero), trackDb, fadeIn, fadeOut, crossIn, crossOut);
        return gain.ClipDb is null && trackDb is null && !gain.HasFades ? null : gain;
    }

    private static Fade Cross(Cut cut, bool fadeOut)
    {
        long start = (cut.At - cut.Before).ToSamples(AudioSampleRate);
        return new Fade(start, (cut.At + cut.After).ToSamples(AudioSampleRate) - start, fadeOut);
    }

    private static long Samples(EffectInstance? audio, string parameter)
    {
        if (audio?.Parameters.GetValueOrDefault(parameter) is not { } p || !double.IsFinite(p.Value) || p.Value <= 0)
        {
            return 0;
        }

        return Time.FromSeconds(Math.Min(p.Value, 24 * 3600)).ToSamples(AudioSampleRate);
    }

    /// <summary>
    /// Ganancia del primer <c>rodaje.audio</c> activo en muestras (del origen en un clip, de la secuencia en una pista);
    /// nula si no lo hay o si vale 0 dB sin keyframes.
    /// </summary>
    /// <param name="offset">Se suma a los keyframes: el inicio en la línea de tiempo del tiempo de una pista anidada.</param>
    private static Curve? GainCurve(IReadOnlyList<EffectInstance> effects, Time offset)
    {
        EffectInstance? audio = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        if (audio is null || EffectRegistry.Find(BuiltInEffects.Audio)?.FindParameter(BuiltInEffects.AudioParameters.GainDb) is not { } definition)
        {
            return null;
        }

        Curve curve = Curve.From(audio.Parameters.GetValueOrDefault(definition.Name), definition.DefaultValue, definition.Clamp,
            ticks => Time.FromTicks(ticks + offset.Ticks).ToSamples(AudioSampleRate));
        return curve.IsConstant && curve.First == 0 ? null : curve;
    }
}
