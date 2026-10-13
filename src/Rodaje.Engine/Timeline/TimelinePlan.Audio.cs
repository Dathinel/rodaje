using Rodaje.Audio.Processing;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.Engine.Timeline;

internal sealed partial class TimelinePlan
{
    /// <summary>
    /// Un compuesto de audio se mezcla aparte si tiene volumen, paneo, fundidos o inserciones, una transición, o si alguna
    /// pista de su secuencia lleva fader o inserciones; si no, se despliega en sus clips.
    /// </summary>
    private static bool AudioGroup(Project project, Track track, Clip compound)
    {
        EffectInstance? audio = compound.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        return GainCurve(compound.Effects, Time.Zero) is not null || Fader(compound.Effects, Time.Zero) is not null
            || AudioEffects(compound.Effects) is not null
            || Samples(audio, BuiltInEffects.AudioParameters.FadeIn) > 0 || Samples(audio, BuiltInEffects.AudioParameters.FadeOut) > 0
            || TouchesTransition(track, compound)
            || (project.FindSequence(compound.SequenceId!) is { } nested
                && nested.AudioTracks.Exists(t => !t.Muted && (Fader(t.Effects, Time.Zero) is not null || AudioEffects(t.Effects) is not null)));
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
                    clips.Add(new AudioClipPlan(clip.Id, default, start, end, sourceIn, Gain(clip, entering, leaving), null,
                        AudioEffects(clip.Effects))
                    {
                        Bus = Bus(clip, clip.Start - before, clip.End + after, depth),
                        Automation = Automation(clip.Effects, Time.Zero),
                    });
                    continue;
                }

                clips.Add(new AudioClipPlan(clip.Id, Key(media[clip.MediaId], projectDirectory), start, end, sourceIn,
                    Gain(clip, entering, leaving), Retime(clip), AudioEffects(clip.Effects)) { Automation = Automation(clip.Effects, Time.Zero) });
            }

            return new AudioTrackPlan(track.Id, JoinSplitPieces(clips, included, cuts), AudioEffects(track.Effects))
            {
                Automation = Automation(track.Effects, offset),
                Fader = Fader(track.Effects, offset),
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
    /// Las inserciones activas en el orden de la pila (un ecualizador que no cambia nada no entra); nulo si no hay. Sus
    /// parámetros no se animan (vale su valor fijo), salvo umbral, relación y mezcla del primer compresor
    /// (<see cref="Automation"/>), que además es el que escucha el sidechain.
    /// </summary>
    internal static AudioEffectsSettings? AudioEffects(IReadOnlyList<EffectInstance> effects)
    {
        List<AudioInsert>? inserts = null;
        EffectInstance? compressor = null;
        foreach (EffectInstance effect in effects)
        {
            if (!effect.Enabled || Insert(effect) is not { } settings)
            {
                continue;
            }

            if (settings is CompressorSettings)
            {
                compressor ??= effect;
            }

            (inserts ??= []).Add(new AudioInsert(effect.Id, settings));
        }

        if (inserts is null)
        {
            return null;
        }

        string? sidechain = compressor?.Options.GetValueOrDefault(BuiltInEffects.CompressorOptions.Sidechain);
        return new AudioEffectsSettings(inserts) { SidechainTrackId = string.IsNullOrEmpty(sidechain) ? null : sidechain };
    }

    /// <summary>Los ajustes de una inserción de audio; nulo si no es una o si no cambia nada.</summary>
    private static AudioProcessorSettings? Insert(EffectInstance effect)
    {
        double V(string name) => Value(effect, name);
        bool On(string name) => Value(effect, name) >= 0.5;
        switch (effect.Type)
        {
            case BuiltInEffects.Equalizer:
                return Equalizer(effect);
            case BuiltInEffects.Compressor:
                return Dynamics(effect);
            case BuiltInEffects.Gate:
                return new GateSettings(V(BuiltInEffects.GateParameters.Threshold), V(BuiltInEffects.GateParameters.Range),
                    V(BuiltInEffects.GateParameters.Ratio), V(BuiltInEffects.GateParameters.Attack), V(BuiltInEffects.GateParameters.Hold),
                    V(BuiltInEffects.GateParameters.Release))
                {
                    HysteresisDb = V(BuiltInEffects.GateParameters.Hysteresis),
                    SidechainHighPassHz = V(BuiltInEffects.GateParameters.SidechainHighPass),
                };
            case BuiltInEffects.DeEsser:
                return new DeEsserSettings(V(BuiltInEffects.DeEsserParameters.Frequency), V(BuiltInEffects.DeEsserParameters.Threshold),
                    V(BuiltInEffects.DeEsserParameters.Range)) { SplitBand = On(BuiltInEffects.DeEsserParameters.SplitBand) };
            case BuiltInEffects.Reverb:
                return new ReverbSettings(V(BuiltInEffects.ReverbParameters.PreDelay), V(BuiltInEffects.ReverbParameters.Size) / 100,
                    V(BuiltInEffects.ReverbParameters.Decay), V(BuiltInEffects.ReverbParameters.Damping), V(BuiltInEffects.ReverbParameters.Width) / 100,
                    V(BuiltInEffects.ReverbParameters.LowCut), V(BuiltInEffects.ReverbParameters.Mix) / 100);
            case BuiltInEffects.Delay:
                return new DelaySettings(V(BuiltInEffects.DelayParameters.Time), V(BuiltInEffects.DelayParameters.Feedback) / 100,
                    V(BuiltInEffects.DelayParameters.Mix) / 100)
                {
                    Sync = On(BuiltInEffects.DelayParameters.Sync),
                    Tempo = V(BuiltInEffects.DelayParameters.Tempo),
                    Division = (DelayDivision)(int)Math.Round(V(BuiltInEffects.DelayParameters.Division)),
                    PingPong = On(BuiltInEffects.DelayParameters.PingPong),
                    LowCutHz = V(BuiltInEffects.DelayParameters.LowCut),
                    HighCutHz = V(BuiltInEffects.DelayParameters.HighCut),
                };
            case BuiltInEffects.Saturation:
                return new SaturationSettings(V(BuiltInEffects.SaturationParameters.Drive), V(BuiltInEffects.SaturationParameters.Character) / 100,
                    V(BuiltInEffects.SaturationParameters.Tone), V(BuiltInEffects.SaturationParameters.Output), V(BuiltInEffects.SaturationParameters.Mix) / 100);
            case BuiltInEffects.GraphicEqualizer:
            {
                IReadOnlyList<string> b = BuiltInEffects.GraphicEqualizerParameters.Bands;
                var graphic = new GraphicEqualizerSettings(V(b[0]), V(b[1]), V(b[2]), V(b[3]), V(b[4]), V(b[5]), V(b[6]), V(b[7]), V(b[8]), V(b[9]))
                {
                    OutputDb = V(BuiltInEffects.GraphicEqualizerParameters.Output),
                };
                return b.All(n => V(n) == 0) && graphic.OutputDb == 0 ? null : graphic;
            }

            case BuiltInEffects.PitchShift:
                return new PitchShiftSettings(V(BuiltInEffects.PitchShiftParameters.Semitones), V(BuiltInEffects.PitchShiftParameters.Cents),
                    V(BuiltInEffects.PitchShiftParameters.Mix) / 100);
            case BuiltInEffects.Limiter:
                return new LimiterSettings(V(BuiltInEffects.LimiterParameters.Ceiling), V(BuiltInEffects.LimiterParameters.InputGain),
                    V(BuiltInEffects.LimiterParameters.Release));
            default:
                return null;
        }
    }

    private static EqualizerSettings? Equalizer(EffectInstance eq)
    {
        {
            var equalizer = new EqualizerSettings(
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
            return equalizer.IsIdentity ? null : equalizer;
        }
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
    /// El fader de una pista, un bus o un clip compuesto: volumen, paneo y balance del primer <c>rodaje.audio</c> activo, en
    /// muestras de la línea de tiempo desde <paramref name="offset"/>; nulo si los tres quedan en su valor neutro.
    /// </summary>
    internal static FaderPlan? Fader(IReadOnlyList<EffectInstance> effects, Time offset)
    {
        EffectInstance? audio = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        if (audio is null)
        {
            return null;
        }

        Curve? db = GainCurve(effects, offset);
        Curve? pan = AudioCurve(audio, A.Pan, offset);
        Curve? balance = AudioCurve(audio, A.Balance, offset);
        return db is null && pan is null && balance is null ? null : new FaderPlan(db, pan, balance);
    }

    /// <summary>Un parámetro de <c>rodaje.audio</c> en muestras desde <paramref name="offset"/>; nulo si vale 0 sin keyframes.</summary>
    private static Curve? AudioCurve(EffectInstance? audio, string name, Time offset)
    {
        if (audio is null || !audio.Parameters.TryGetValue(name, out Parameter? parameter)
            || EffectRegistry.Find(BuiltInEffects.Audio)?.FindParameter(name) is not { } definition)
        {
            return null;
        }

        Curve curve = Curve.From(parameter, definition.DefaultValue, definition.Clamp,
            ticks => Time.FromTicks(ticks + offset.Ticks).ToSamples(AudioSampleRate));
        return curve.IsConstant && curve.First == 0 ? null : curve;
    }

    /// <summary>
    /// Arma los buses de la secuencia principal y deja en cada pista su salida, sus envíos y si suena directo, según los
    /// silencios y los solos: con algún solo suenan las pistas con solo y las que salen a un bus con solo, y los buses con
    /// solo o que reciben de una pista con solo. Una pista que llega a un bus con retraso se mezcla por delante eso.
    /// </summary>
    internal static (List<AudioTrackPlan> Tracks, List<MixBusPlan> Buses) Route(Sequence sequence, IReadOnlyList<Track> sources, List<AudioTrackPlan> tracks)
    {
        List<AudioBus> declared = sequence.AudioBuses;
        if (declared.Count == 0 && !sources.Any(t => t.Solo))
        {
            return (tracks, []);
        }

        var byId = declared.ToDictionary(b => b.Id, StringComparer.Ordinal);
        bool soloing = sources.Any(t => t.Solo) || declared.Any(b => b.Solo);
        bool BusSoloed(string? id) => id is not null && byId.TryGetValue(id, out AudioBus? bus) && bus.Solo;
        var audibleBuses = new HashSet<string>(StringComparer.Ordinal);
        foreach (AudioBus bus in declared)
        {
            bool fed = sources.Any(t => t.Solo && (t.Output == bus.Id || t.Sends.Exists(s => s.BusId == bus.Id && !s.Muted)));
            if (!bus.Muted && (!soloing || bus.Solo || fed))
            {
                audibleBuses.Add(bus.Id);
            }
        }

        var routed = new List<AudioTrackPlan>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            Track source = sources[i];
            string? output = source.Output is { } o && byId.ContainsKey(o) ? o : null;
            bool open = !soloing || source.Solo || BusSoloed(output);
            bool direct = open && (output is null || audibleBuses.Contains(output));
            List<AudioSendPlan> sends = [.. source.Sends
                .Where(s => !s.Muted && s.GainDb > AudioSend.MinGainDb && audibleBuses.Contains(s.BusId) && (open || byId[s.BusId].Solo))
                .Select(s => new AudioSendPlan(s.BusId, s.GainDb, s.PreFader))];
            routed.Add(tracks[i] with { Output = output, Sends = sends, Direct = direct });
        }

        var buses = new List<MixBusPlan>();
        foreach (AudioBus bus in declared)
        {
            if (audibleBuses.Contains(bus.Id))
            {
                buses.Add(new MixBusPlan(bus.Id, AudioEffects(bus.Effects) is { } fx ? fx with { SidechainTrackId = null } : null, Fader(bus.Effects, Time.Zero)));
            }
        }

        var latency = buses.ToDictionary(b => b.BusId, b => b.Latency, StringComparer.Ordinal);
        for (int i = 0; i < routed.Count; i++)
        {
            AudioTrackPlan track = routed[i];
            int ahead = track.Direct && track.Output is { } bus ? latency[bus] : 0;
            foreach (AudioSendPlan send in track.Sends)
            {
                ahead = Math.Max(ahead, latency[send.BusId]);
            }

            routed[i] = track with { BusAhead = ahead };
        }

        return (routed, buses);
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

    /// <summary>Volumen del clip: su <c>rodaje.audio</c> (ganancia, paneo y balance) y los fundidos; nulo si suena tal cual.</summary>
    private static GainPlan? Gain(Clip clip, Cut? entering, Cut? leaving)
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

        var gain = new GainPlan(GainCurve(clip.Effects, Time.Zero), null, fadeIn, fadeOut, crossIn, crossOut)
        {
            Pan = AudioCurve(audio, A.Pan, Time.Zero),
            Balance = AudioCurve(audio, A.Balance, Time.Zero),
        };
        return gain.ClipDb is null && !gain.HasFades && !gain.HasPan ? null : gain;
    }

    /// <summary>El fundido de un lado de la transición: la forma de su tipo (potencia constante si no es de audio) y su curva.</summary>
    private static Fade Cross(Cut cut, bool fadeOut)
    {
        long start = (cut.At - cut.Before).ToSamples(AudioSampleRate);
        TransitionInstance transition = cut.Transition;
        return new Fade(start, (cut.At + cut.After).ToSamples(AudioSampleRate) - start, fadeOut)
        {
            Shape = Fade.ShapeOf(transition.Type),
            Curve = transition.Curve,
            Ease = transition.Ease,
        };
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
