using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Packages;
using MaskOptions = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.App.Services.Inspector;

// Las listas de los desplegables de cada panel del inspector, sin WinUI: la vista las arma aquí en cada refresco y las
// muestra con ComboBoxChoices; las pruebas arman las mismas para comprobar que elegir cualquier opción y refrescar no
// rehace el desplegable (tarea 91). Cada función devuelve la lista y la posición que se muestra elegida (−1: ninguna).

/// <summary>Los desplegables de la pestaña «Dinámica»: a qué se aplica, el preajuste y el sidechain.</summary>
public static class DynamicsChoices
{
    /// <summary>
    /// Lo que hay para elegir (el clip de audio <paramref name="clip"/>, si hay, cada pista de audio, cada bus y la mezcla) y
    /// el dueño que se muestra: <paramref name="chosen"/> si sigue en la lista; si no, la mezcla sin clip o el primero.
    /// </summary>
    public static (ChoiceList<DynamicsOwner> List, DynamicsOwner Chosen) Targets(Project project, Sequence sequence, string? clip, DynamicsOwner? chosen)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        var owners = new List<DynamicsOwner>();
        if (clip is not null)
        {
            owners.Add(new DynamicsOwner(DynamicsOwnerKind.Clip, clip));
        }

        owners.AddRange(sequence.AudioTracks.Select(t => new DynamicsOwner(DynamicsOwnerKind.Track, t.Id)));
        owners.AddRange(sequence.AudioBuses.Select(b => new DynamicsOwner(DynamicsOwnerKind.Bus, b.Id)));
        owners.Add(DynamicsOwner.Mix(sequence));
        if (chosen is null || !owners.Contains(chosen))
        {
            chosen = owners.Count > 1 && clip is null ? DynamicsOwner.Mix(sequence) : owners[0];
        }

        var targets = new ChoiceList<DynamicsOwner>();
        foreach (DynamicsOwner owner in owners)
        {
            targets.Add(TargetName(project, sequence, owner), owner);
        }

        return (targets, chosen);
    }

    /// <summary>El texto de cada dueño en el desplegable de destino.</summary>
    public static string TargetName(Project project, Sequence sequence, DynamicsOwner owner)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(owner);
        return owner.Kind switch
        {
            DynamicsOwnerKind.Clip => "Clip «" + ClipLabels.Name(project, sequence.GetClip(owner.Id).Clip, "audio") + "»",
            DynamicsOwnerKind.Track => "Pista «" + sequence.GetTrack(owner.Id).Name + "»",
            DynamicsOwnerKind.Bus => "Bus «" + (sequence.FindBus(owner.Id)?.Name ?? "") + "»",
            _ => "Mezcla",
        };
    }

    /// <summary>
    /// Los preajustes: el valor de cada elemento es −1 «Sin compresor», el índice en <see cref="AudioPresets.All"/> o −2
    /// «Personalizado»; se elige «Sin compresor» si no hay compresor, el preajuste que coincide o «Personalizado».
    /// </summary>
    public static (ChoiceList<int> List, int Selected) Presets(bool hasCompressor, AudioPreset? matching)
    {
        var presets = new ChoiceList<int>();
        presets.Add("Sin compresor", -1);
        for (int i = 0; i < AudioPresets.All.Count; i++)
        {
            presets.Add(AudioPresets.All[i].Name, i);
        }

        presets.Add("Personalizado", -2);
        int selected = !hasCompressor ? 0 : matching is null ? presets.Count - 1 : 1 + AudioPresets.All.ToList().IndexOf(matching);
        return (presets, selected);
    }

    /// <summary>
    /// El sidechain: «Su propio audio» (nulo), cada pista candidata por su id y, si el sidechain guardado ya no es una de
    /// ellas, «Una pista que ya no está»; se elige el guardado o, si no hay, «Su propio audio».
    /// </summary>
    public static (ChoiceList<string?> List, int Selected) Sidechains(IEnumerable<Track> candidates, string? key)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var sidechains = new ChoiceList<string?>();
        sidechains.Add("Su propio audio", null);
        foreach (Track track in candidates)
        {
            sidechains.Add("Pista «" + track.Name + "»", track.Id);
        }

        if (!string.IsNullOrEmpty(key) && !sidechains.Contains(key))
        {
            sidechains.Add("Una pista que ya no está", key);
        }

        return (sidechains, Math.Max(0, sidechains.IndexOf(string.IsNullOrEmpty(key) ? null : key)));
    }
}

/// <summary>El desplegable de tipo de la pestaña «Transición».</summary>
public static class TransitionChoices
{
    /// <summary>
    /// Los tipos de la pista (<see cref="TransitionEditor.TypesFor"/>) y, fuera de las de audio, las transiciones de los
    /// paquetes instalados; el tipo actual se elige y, si no está (un tipo de imagen en una pista de audio, de antes del
    /// formato 12, o un paquete que falta), se añade al final.
    /// </summary>
    public static (ChoiceList<(string Type, string Preset)> List, int Selected) Types(TrackKind kind, PackageScan installed, string type, string preset)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(preset);
        var types = new ChoiceList<(string Type, string Preset)>();
        foreach (EffectDefinition definition in TransitionEditor.TypesFor(kind))
        {
            types.Add(EffectUi.Name(definition.Type), (definition.Type, ""));
        }

        if (kind != TrackKind.Audio)
        {
            foreach (PackageItemEntry entry in installed.Items(ItemKind.Transition))
            {
                string source = entry.Package.Origin == PackageOrigin.Own ? "Mía" : entry.Package.Manifest.Name;
                types.Add($"{entry.Item.Name} · {source}", (BuiltInTransitions.Preset, entry.Reference.ToString()));
            }
        }

        // Una transición propia guardada con su id de la v1.0.0 se elige por el actual.
        int index = types.IndexOf((type, preset.Length > 0 ? installed.Canonical(preset, ItemKind.Transition) : preset));
        if (index < 0)
        {
            types.Add(preset.Length > 0 ? preset + " (falta el paquete)" : EffectUi.Name(type), (type, preset));
            index = types.Count - 1;
        }

        return (types, index);
    }
}

/// <summary>El desplegable de LUT de la pestaña «Filtros» (corrección de color).</summary>
public static class LutChoices
{
    /// <summary>El último elemento: abre el diálogo para elegir un archivo (su valor es nulo).</summary>
    public const string LoadCubeLabel = "Cargar .cube…";

    /// <summary>
    /// «Sin LUT» (vacío), las LUT de los paquetes instalados, la actual si no es de ellas (un archivo o un paquete que
    /// falta) y «Cargar .cube…» (nulo); se elige la actual.
    /// </summary>
    public static (ChoiceList<string?> List, int Selected) Build(PackageScan installed, string current)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(current);
        // Una LUT propia guardada con su id de la v1.0.0 se elige por el actual.
        current = installed.Canonical(current, ItemKind.Lut);
        var luts = new ChoiceList<string?>();
        luts.Add("Sin LUT", "");
        foreach (PackageItemEntry entry in installed.Items(ItemKind.Lut))
        {
            luts.Add($"{entry.Item.Name} · {entry.Package.Manifest.Name}", entry.Reference.ToString());
        }

        if (current.Length > 0 && !luts.Contains(current))
        {
            string name = LutReference.DisplayName(current, installed);
            luts.Add(LutReference.IsFile(current) ? name + " (archivo)" : name + " (falta el paquete)", current);
        }

        luts.Add(LoadCubeLabel, null);
        return (luts, luts.IndexOf(current));
    }
}

/// <summary>Los desplegables «Aplicar a…» de la pestaña «Seguimiento»: el uso y el destino.</summary>
public static class TrackingChoices
{
    /// <summary>Cada uso con su texto, en el orden del desplegable.</summary>
    public static readonly IReadOnlyList<(TrackUse Use, string Label)> Uses =
    [
        (TrackUse.FollowPosition, "Otro clip sigue la posición"),
        (TrackUse.FollowAll, "Otro clip sigue posición, escala y giro"),
        (TrackUse.CornerPin, "Anclaje de esquinas en otro clip (pantalla, cartel)"),
        (TrackUse.Mask, "Una máscara de este clip sigue la forma"),
        (TrackUse.BlurZone, "Desenfocar la zona seguida (caras, matrículas)"),
        (TrackUse.PixelateZone, "Pixelar la zona seguida"),
        (TrackUse.Stabilize, "Estabilizar este clip en lo seguido"),
    ];

    /// <summary>Los usos que admite un seguimiento de <paramref name="kind"/>; se elige <paramref name="chosen"/> si sigue, o el primero.</summary>
    public static (ChoiceList<TrackUse> List, int Selected) ForKind(PointTrackKind kind, TrackUse? chosen)
    {
        var uses = new ChoiceList<TrackUse>();
        foreach ((TrackUse use, string label) in Uses)
        {
            if (TrackApply.Supports(kind, use))
            {
                uses.Add(label, use);
            }
        }

        return (uses, chosen is { } c && uses.IndexOf(c) is >= 0 and var index ? index : 0);
    }

    /// <summary>
    /// A qué se aplica <paramref name="use"/> en <paramref name="clip"/>: los clips de video que se le solapan (para seguir o
    /// anclar; con un texto de ayuda si no hay ninguno), sus máscaras (con «Una máscara nueva») o nada. Se elige
    /// <paramref name="chosen"/> si sigue, o el primero.
    /// </summary>
    public static (ChoiceList<string?> List, int Selected) Targets(Project project, Sequence sequence, Clip clip, TrackUse use, string? chosen)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);
        var targets = new ChoiceList<string?>();
        if (use is TrackUse.FollowPosition or TrackUse.FollowAll or TrackUse.CornerPin)
        {
            foreach (Track track in sequence.VideoTracks)
            {
                foreach (Clip other in track.Clips)
                {
                    if (other.Id != clip.Id && other.Start < clip.End && other.End > clip.Start
                        && (use != TrackUse.CornerPin || (other.Generator is null && !other.IsCompound)))
                    {
                        targets.Add($"{ClipLabels.Name(project, other, "Clip")} ({track.Name})", other.Id);
                    }
                }
            }

            if (targets.Count == 0)
            {
                targets.Add(use == TrackUse.CornerPin ? "Pon un video o una imagen encima de este clip" : "Pon un título, pegatina o imagen encima de este clip", null);
            }
        }
        else if (use == TrackUse.Mask)
        {
            targets.Add("Una máscara nueva (elipse)", null);
            int number = 0;
            foreach (EffectInstance mask in clip.Effects.Where(e => e.Type == BuiltInEffects.Mask))
            {
                number++;
                targets.Add(mask.Options.GetValueOrDefault(MaskOptions.Name) is { Length: > 0 } name ? name : $"Máscara {number}", mask.Id);
            }
        }

        return (targets, targets.Count == 0 ? -1 : Math.Max(0, targets.IndexOf(chosen)));
    }
}

/// <summary>El desplegable de estilos de la pestaña «Texto».</summary>
public static class TextStyleChoices
{
    /// <summary>Los estilos de texto que se ofrecen (los de fábrica por su nombre; los de paquetes, con su origen).</summary>
    public static ChoiceList<LibraryElement> Build(PackageScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var styles = new ChoiceList<LibraryElement>();
        foreach (LibraryElement element in LibraryElements.For(LibraryCategory.Text, scan))
        {
            styles.Add(element.IsBuiltIn ? element.Name : $"{element.Name} · {element.Source}", element);
        }

        return styles;
    }

    /// <summary>
    /// El estilo de <paramref name="styles"/> que tiene el título: el de su origen o, sin origen, el de fábrica si no se
    /// cambió; −1 si ninguno.
    /// </summary>
    public static int IndexOf(ChoiceList<LibraryElement> styles, TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(styles);
        ArgumentNullException.ThrowIfNull(style);
        for (int i = 0; i < styles.Count; i++)
        {
            LibraryElement e = styles.Values[i];
            if (style.Source is { } source ? e.IsReferredBy(source) : e.IsBuiltIn && style.ContentEquals(TitleStyles.Default()))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>El desplegable de destino de la pestaña «Máscaras».</summary>
public static class MaskTargetChoices
{
    /// <summary>
    /// El valor de «Zona de los efectos con “Dónde”» (el uso «limitar efectos» de color y filtros, no un efecto). Empieza con
    /// U+0001 para que no coincida nunca con el id de un efecto.
    /// </summary>
    public const string EffectsZone = "\u0001zona";

    /// <summary>
    /// La capa (vacío), la zona de los efectos, cada efecto de <paramref name="clip"/> que admite máscara y, si el destino
    /// guardado ya no está, «Un efecto que ya no está»; se elige el destino de <paramref name="mask"/>.
    /// </summary>
    public static (ChoiceList<string> List, int Selected) Build(Clip clip, EffectInstance mask)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(mask);
        var targets = new ChoiceList<string>();
        targets.Add("La capa (recorta el clip)", "");
        targets.Add("Zona de los efectos con «Dónde» (no recorta)", EffectsZone);
        foreach (EffectInstance effect in clip.Effects)
        {
            if (MaskEdits.CanBeMasked(effect.Type))
            {
                targets.Add("Solo «" + EffectUi.Name(effect.Type) + "»" + (effect.Enabled ? "" : " (desactivado)"), effect.Id);
            }
        }

        string current = mask.Options.GetValueOrDefault(MaskOptions.Target) is { Length: > 0 } target ? target
            : mask.Options.GetValueOrDefault(MaskOptions.Use) == "effects" ? EffectsZone : "";
        if (!targets.Contains(current))
        {
            targets.Add("Un efecto que ya no está", current);
        }

        return (targets, targets.IndexOf(current));
    }
}
