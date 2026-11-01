using System.Collections.Frozen;
using System.Collections.Immutable;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using CK = Rodaje.Core.Model.BuiltInEffects.ChromaKeyParameters;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.EqualizerParameters;
using Hq = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;
using W = Rodaje.Core.Model.BuiltInEffects.ColorWheelsParameters;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;
using TP = Rodaje.Core.Model.BuiltInTransitions.CommonParameters;

namespace Rodaje.Core.Effects;

/// <summary>
/// Los tipos de efecto y de transición que entiende el motor, sin reflexión (apto para AOT). Un tipo que no está aquí
/// (de un paquete o de otro programa) se conserva en el proyecto, pero no se aplica.
/// </summary>
/// <remarks>Los códigos son estables: el render los usa como índice y una prueba exige que no se repitan.</remarks>
public static class EffectRegistry
{
    private const double Inf = double.PositiveInfinity;

    /// <summary>Los 13 modos de mezcla, en el orden de <c>Rodaje.Playback.Composition.BlendMode</c>.</summary>
    public static ImmutableArray<string> BlendModes { get; } =
    [
        "normal", "add", "multiply", "screen", "overlay", "softLight", "hardLight",
        "darken", "lighten", "difference", "exclusion", "colorDodge", "colorBurn",
    ];

    /// <summary>Hacia dónde sale el clip en <see cref="BuiltInTransitions.Push"/> y <see cref="BuiltInTransitions.Bounce"/>.</summary>
    public static ImmutableArray<string> PushDirections { get; } = ["left", "right", "up", "down"];

    /// <summary>Degradados de fábrica de <see cref="BuiltInTransitions.Luma"/>, en el orden de su índice en el render.</summary>
    public static ImmutableArray<string> LumaGradients { get; } = ["linear", "radial", "angular", "diamond", "noise", "bars"];

    /// <summary>Borde de color de un barrido: ancho (0 sin borde) y color, blanco por defecto.</summary>
    private static ParameterDefinition[] Border() =>
    [
        new(TP.BorderWidth, 0, 0, 0.2, Animatable: false),
        new(TP.BorderRed, 1, 0, 1, Animatable: false),
        new(TP.BorderGreen, 1, 0, 1, Animatable: false),
        new(TP.BorderBlue, 1, 0, 1, Animatable: false),
    ];

    /// <summary>Recorte de <see cref="BuiltInEffects.Stabilize"/>: automático o ninguno.</summary>
    public static ImmutableArray<string> StabilizeCrops { get; } = ["auto", "off"];

    /// <summary>Formas de <see cref="BuiltInEffects.Mask"/>, en el orden de <see cref="MaskShape"/>.</summary>
    public static ImmutableArray<string> MaskShapes { get; } = ["rectangle", "ellipse", "path", "image", "bezier", "stroke"];

    /// <summary>Cómo se combina una máscara con las anteriores del mismo destino, en el orden de <see cref="MaskMode"/>.</summary>
    public static ImmutableArray<string> MaskModes { get; } = ["add", "subtract", "intersect", "difference"];

    /// <summary>Proporciones de <see cref="BuiltInEffects.Crop"/>: libre o ancho:alto del recorte en pantalla.</summary>
    public static ImmutableArray<string> CropAspects { get; } = ["free", "16:9", "9:16", "1:1", "4:5", "4:3"];

    /// <summary>Tipos de <see cref="BuiltInEffects.Blur"/>.</summary>
    public static ImmutableArray<string> BlurKinds { get; } = ["gaussian", "lens", "directional", "radial"];

    /// <summary>Dónde se aplica un efecto de imagen: en toda la imagen, o dentro o fuera de las máscaras que limitan efectos.</summary>
    public static ImmutableArray<string> Regions { get; } = ["all", "inside", "outside"];

    /// <summary>Para qué sirve una máscara: recortar la capa o marcar dónde van los efectos.</summary>
    public static ImmutableArray<string> MaskUses { get; } = ["cut", "effects"];

    /// <summary>Modos de <see cref="BuiltInEffects.Mirror"/>: qué mitad se refleja, las cuatro, o un mosaico.</summary>
    public static ImmutableArray<string> MirrorModes { get; } = ["left", "right", "top", "bottom", "quad", "tiles"];

    /// <summary>Qué sigue un <see cref="BuiltInEffects.Track"/>: un punto, dos (escala y giro), cuatro esquinas (perspectiva) o una región.</summary>
    public static ImmutableArray<string> TrackKinds { get; } = ["point", "two", "corners", "region"];

    /// <summary>Lo más que se corre una esquina de <see cref="BuiltInEffects.CornerPin"/>, en píxeles de la secuencia.</summary>
    public const double MaxCornerOffset = 100_000;
    /// <summary>Proyecciones de <see cref="BuiltInEffects.Reframe360"/>: una cámara normal o el pequeño planeta.</summary>
    public static ImmutableArray<string> Reframe360Projections { get; } = ["rectilinear", "stereographic"];

    /// <summary>Modos de <see cref="BuiltInEffects.ChromaticAberration"/>.</summary>
    public static ImmutableArray<string> AberrationModes { get; } = ["radial", "lateral"];

    private static OptionDefinition Region => OptionDefinition.Choice(BuiltInEffects.RegionOptions.Region, "all", [.. Regions]);

    /// <summary>Duración con la que se suelta una transición.</summary>
    public static Time DefaultTransitionDuration { get; } = Time.FromSeconds(0.5);

    /// <summary>Inclinación máxima de <see cref="T.SkewX"/> y <see cref="T.SkewY"/>, en grados (a 90° la capa se aplasta).</summary>
    public const double MaxSkew = 85;

    /// <summary>
    /// Lo más que se corre una capa o su ancla (<see cref="T.X"/>, <see cref="T.Y"/>, <see cref="T.AnchorX"/>,
    /// <see cref="T.AnchorY"/>), en píxeles de la secuencia: cientos de cuadros fuera de la vista. Más allá la GPU, que
    /// trabaja en float, recibía infinitos (y un keyframe de −1e308 a 1e308 se desbordaba al interpolar).
    /// </summary>
    public const double MaxOffset = 1_000_000;

    /// <summary>Lo más que gira una capa o una máscara, en grados (unas 2.800 vueltas).</summary>
    public const double MaxRotation = 1_000_000;

    /// <summary>Lo más lejos que va el centro de una máscara (<see cref="M.X"/>, <see cref="M.Y"/>), en fracciones de la imagen.</summary>
    public const double MaxMaskOffset = 10_000;

    /// <summary>Ganancia que se trata como silencio: el mínimo de <see cref="A.GainDb"/>.</summary>
    public const double SilenceDb = -60;

    public static ImmutableArray<EffectDefinition> All { get; } =
    [
        new(BuiltInEffects.Transform, 1, EffectTarget.Video, EffectStage.Transform,
        [
            new(T.X, 0, -MaxOffset, MaxOffset),
            new(T.Y, 0, -MaxOffset, MaxOffset),
            new(T.Scale, 1, 0, 100),
            new(T.Rotation, 0, -MaxRotation, MaxRotation),
            new(T.Opacity, 1, 0, 1),
            new(T.AnchorX, 0, -MaxOffset, MaxOffset),
            new(T.AnchorY, 0, -MaxOffset, MaxOffset),
            new(T.ScaleX, 1, 0, 100),
            new(T.ScaleY, 1, 0, 100),
            new(T.SkewX, 0, -MaxSkew, MaxSkew),
            new(T.SkewY, 0, -MaxSkew, MaxSkew),
            new(T.ShutterAngle, 0, 0, 720, Animatable: false),
            new(T.ShutterPhase, 0, -360, 360, Animatable: false),
            new(T.MotionSamples, T.DefaultMotionSamples, 2, 64, Animatable: false),
            new(T.HdrReferenceNits, 0, 0, 10000),
        ],
        [
            OptionDefinition.Choice(BuiltInEffects.TransformOptions.FlipX, "off", "off", "on"),
            OptionDefinition.Choice(BuiltInEffects.TransformOptions.FlipY, "off", "off", "on"),
            OptionDefinition.Choice(BuiltInEffects.TransformOptions.HdrExpand, "off", "off", "on"),
        ]),
        new(BuiltInEffects.Color, 2, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(C.Exposure, 0, -4, 4),
            new(C.Contrast, 0, -1, 1),
            new(C.Saturation, 0, -1, 1),
            new(C.Temperature, 0, -1, 1),
            new(C.Tint, 0, -1, 1),
            new(C.Highlights, 0, -1, 1),
            new(C.Shadows, 0, -1, 1),
            new(C.LutIntensity, 1, 0, 1),
            new(C.SkinProtection, 0, 0, 1),
        ],
        [OptionDefinition.Reference(BuiltInEffects.ColorOptions.Lut, allowsFile: true)]),
        new(BuiltInEffects.Vignette, 3, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(BuiltInEffects.VignetteParameters.Amount, 0.5, 0, 1),
            new(BuiltInEffects.VignetteParameters.Size, 0.5, 0, 1),
            new(BuiltInEffects.VignetteParameters.Softness, 0.5, 0, 1),
            new(BuiltInEffects.VignetteParameters.Roundness, 0, 0, 1),
            new(BuiltInEffects.VignetteParameters.CenterX, 0.5, -0.5, 1.5),
            new(BuiltInEffects.VignetteParameters.CenterY, 0.5, -0.5, 1.5),
        ]),
        new(BuiltInEffects.Blur, 4, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.BlurParameters.Radius, 10, 0, 500),
            new(BuiltInEffects.BlurParameters.Angle, 0, -360, 360),
            new(BuiltInEffects.BlurParameters.CenterX, 0.5, -0.5, 1.5),
            new(BuiltInEffects.BlurParameters.CenterY, 0.5, -0.5, 1.5),
        ],
        [OptionDefinition.Choice(BuiltInEffects.BlurOptions.Kind, "gaussian", [.. BlurKinds]), Region]),
        new(BuiltInEffects.Sharpen, 5, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.SharpenParameters.Amount, 0.5, 0, 4),
            new(BuiltInEffects.SharpenParameters.Radius, 1, 0.5, 10),
            new(BuiltInEffects.SharpenParameters.Threshold, 0, 0, 0.25),
        ],
        [Region]),
        new(BuiltInEffects.Blend, 6, EffectTarget.Video, EffectStage.Blend, [],
        [OptionDefinition.Choice(BuiltInEffects.BlendOptions.Mode, "normal", [.. BlendModes])]),
        new(BuiltInEffects.Audio, 7, EffectTarget.Audio, EffectStage.Audio,
        [
            new(A.GainDb, 0, SilenceDb, 24),
            new(A.FadeIn, 0, 0, Inf, Animatable: false),
            new(A.FadeOut, 0, 0, Inf, Animatable: false),
            new(A.Pan, 0, -100, 100),
            new(A.Balance, 0, -100, 100),
        ]),
        new(BuiltInEffects.Speed, 8, EffectTarget.Video | EffectTarget.Audio, EffectStage.Deferred) { Hidden = true },
        new(BuiltInEffects.Stabilize, 9, EffectTarget.Video, EffectStage.Transform,
        [
            new(BuiltInEffects.StabilizeParameters.Intensity, 0.5, 0, 1, Animatable: false),
        ],
        [OptionDefinition.Choice(BuiltInEffects.StabilizeOptions.Crop, "auto", [.. StabilizeCrops])]),
        new(BuiltInEffects.Mask, 10, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(M.X, 0.5, -MaxMaskOffset, MaxMaskOffset),
            new(M.Y, 0.5, -MaxMaskOffset, MaxMaskOffset),
            new(M.Width, 0.5, 0, 100),
            new(M.Height, 0.5, 0, 100),
            new(M.Rotation, 0, -MaxRotation, MaxRotation),
            new(M.Feather, 0, 0, 1),
            new(M.Opacity, 1, 0, 1),
            new(M.Expansion, 0, -0.5, 0.5),
            new(M.Roundness, 0, 0, 1),
            new(M.Thickness, 0.02, 0, 0.5),
        ],
        [
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Shape, "rectangle", [.. MaskShapes]),
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Invert, "off", "off", "on"),
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Mode, "intersect", [.. MaskModes]),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Path),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Matte),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.MatteStart),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Points, MaskPath.MaxTextLength),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.ShapeKeys, MaskShapeTrack.MaxTextLength),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Target),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Name),
            OptionDefinition.Text(BuiltInEffects.MaskOptions.Color),
            OptionDefinition.Choice(BuiltInEffects.MaskOptions.Use, "cut", [.. MaskUses]),
        ]),
        new(BuiltInEffects.Crop, 11, EffectTarget.Video, EffectStage.Transform,
        [
            new(BuiltInEffects.CropParameters.Left, 0, 0, CropGeometry.MaxCrop),
            new(BuiltInEffects.CropParameters.Top, 0, 0, CropGeometry.MaxCrop),
            new(BuiltInEffects.CropParameters.Right, 0, 0, CropGeometry.MaxCrop),
            new(BuiltInEffects.CropParameters.Bottom, 0, 0, CropGeometry.MaxCrop),
        ],
        [OptionDefinition.Choice(BuiltInEffects.CropOptions.Aspect, "free", [.. CropAspects])]),
        new(BuiltInEffects.Pixelate, 24, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.PixelateParameters.Size, 24, 2, 256),
        ],
        [Region]),
        new(BuiltInEffects.CornerPin, 60, EffectTarget.Video, EffectStage.Transform,
            [.. BuiltInEffects.CornerPinParameters.All.Select(name => new ParameterDefinition(name, 0, -MaxCornerOffset, MaxCornerOffset))]),
        new(BuiltInEffects.Track, 61, EffectTarget.Video, EffectStage.Deferred,
        [
            new(BuiltInEffects.TrackParameters.Threshold, 0.7, 0.05, 0.99, Animatable: false),
        ],
        [
            OptionDefinition.Choice(BuiltInEffects.TrackOptions.Kind, "point", [.. TrackKinds]),
            OptionDefinition.Text(BuiltInEffects.TrackOptions.Name),
            OptionDefinition.Text(BuiltInEffects.TrackOptions.Points),
            OptionDefinition.Text(BuiltInEffects.TrackOptions.Samples, PointTrack.MaxTextLength),
            OptionDefinition.Text(BuiltInEffects.TrackOptions.Color),
        ]),
        new(BuiltInEffects.ChromaKey, 12, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(CK.Red, 0, 0, 1, Animatable: false),
            new(CK.Green, 0.7, 0, 1, Animatable: false),
            new(CK.Blue, 0.2, 0, 1, Animatable: false),
            new(CK.Tolerance, 0.2, 0, 1),
            new(CK.Softness, 0.1, 0, 1),
            new(CK.Spill, 0.5, 0, 1),
            new(CK.Cleanup, 0, 0, 1),
        ]),
        new(BuiltInEffects.ColorWheels, 13, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(W.LiftRed, 0, -0.5, 0.5),
            new(W.LiftGreen, 0, -0.5, 0.5),
            new(W.LiftBlue, 0, -0.5, 0.5),
            new(W.Lift, 0, -0.5, 0.5),
            new(W.GammaRed, 0, -1, 1),
            new(W.GammaGreen, 0, -1, 1),
            new(W.GammaBlue, 0, -1, 1),
            new(W.Gamma, 0, -1, 1),
            new(W.GainRed, 0, -1, 1),
            new(W.GainGreen, 0, -1, 1),
            new(W.GainBlue, 0, -1, 1),
            new(W.Gain, 0, -1, 1),
            new(W.OffsetRed, 0, -0.5, 0.5),
            new(W.OffsetGreen, 0, -0.5, 0.5),
            new(W.OffsetBlue, 0, -0.5, 0.5),
            new(W.Offset, 0, -0.5, 0.5),
        ]),
        new(BuiltInEffects.Curves, 14, EffectTarget.Video, EffectStage.PerPixel, [],
        [
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.Master),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.Red),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.Green),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.Blue),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.HueHue),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.HueSaturation),
            OptionDefinition.Text(BuiltInEffects.CurvesOptions.LumaSaturation),
        ]),
        new(BuiltInEffects.Grain, 15, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.GrainParameters.Amount, 0.25, 0, 1),
            new(BuiltInEffects.GrainParameters.Size, 1.5, 0.5, 8),
            new(BuiltInEffects.GrainParameters.Color, 0.15, 0, 1),
            new(BuiltInEffects.GrainParameters.Response, 0.6, 0, 1),
        ],
        [Region]) { UsesTime = true },
        new(BuiltInEffects.Glow, 16, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.GlowParameters.Threshold, 0.65, 0, 1),
            new(BuiltInEffects.GlowParameters.Radius, 40, 1, 400),
            new(BuiltInEffects.GlowParameters.Intensity, 0.8, 0, 4),
        ],
        [Region]),
        new(BuiltInEffects.ChromaticAberration, 17, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.ChromaticAberrationParameters.Amount, 4, 0, 100),
            new(BuiltInEffects.ChromaticAberrationParameters.Angle, 0, -360, 360),
        ],
        [OptionDefinition.Choice(BuiltInEffects.ChromaticAberrationOptions.Mode, "radial", [.. AberrationModes]), Region]),
        new(BuiltInEffects.Denoise, 18, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.DenoiseParameters.Strength, 0.5, 0, 1),
            new(BuiltInEffects.DenoiseParameters.Temporal, 0.5, 0, 1),
            new(BuiltInEffects.DenoiseParameters.Detail, 0.5, 0, 1),
        ],
        [Region]),
        new(BuiltInEffects.Mirror, 19, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.MirrorParameters.Axis, 0.5, 0, 1),
            new(BuiltInEffects.MirrorParameters.Tiles, 2, 1, 6),
        ],
        [OptionDefinition.Choice(BuiltInEffects.MirrorOptions.Mode, "left", [.. MirrorModes])]),
        new(BuiltInEffects.Reframe360, 30, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.Reframe360Parameters.Yaw, 0, -180, 180),
            new(BuiltInEffects.Reframe360Parameters.Pitch, 0, -90, 90),
            new(BuiltInEffects.Reframe360Parameters.Roll, 0, -180, 180),
            new(BuiltInEffects.Reframe360Parameters.Fov, 90, 20, 330),
        ],
        [OptionDefinition.Choice(BuiltInEffects.Reframe360Options.Projection, "rectilinear", [.. Reframe360Projections])])
        {
            Feature = EffectFeatures.Video360,
        },
        new(BuiltInEffects.LensDistortion, 22, EffectTarget.Video, EffectStage.Spatial,
        [
            new(BuiltInEffects.LensDistortionParameters.Amount, 0.2, -1, 1),
            new(BuiltInEffects.LensDistortionParameters.Zoom, 1, 0.5, 2),
        ]),
        new(BuiltInEffects.HslQualifier, 23, EffectTarget.Video, EffectStage.PerPixel,
        [
            new(Hq.HueCenter, 120, 0, 360),
            new(Hq.HueWidth, 30, 0, 180),
            new(Hq.HueSoftness, 15, 0, 90),
            new(Hq.SaturationLow, 0.1, 0, 1),
            new(Hq.SaturationHigh, 1, 0, 1),
            new(Hq.LumaLow, 0, 0, 1),
            new(Hq.LumaHigh, 1, 0, 1),
            new(Hq.Softness, 0.1, 0, 0.5),
            new(Hq.HueShift, 0, -180, 180),
            new(Hq.Saturation, 0, -1, 1),
            new(Hq.Exposure, 0, -2, 2),
        ],
        [OptionDefinition.Choice(BuiltInEffects.HslQualifierOptions.Invert, "off", "off", "on")]),

        new(BuiltInEffects.Equalizer, 20, EffectTarget.Audio, EffectStage.Audio,
        [
            new(Q.LowCut, 0, 0, 500, Animatable: false),
            new(Q.LowGain, 0, -18, 18, Animatable: false),
            new(Q.LowFrequency, 120, 20, 1000, Animatable: false),
            new(Q.MidGain, 0, -18, 18, Animatable: false),
            new(Q.MidFrequency, 500, 100, 8000, Animatable: false),
            new(Q.MidQ, 1, 0.1, 10, Animatable: false),
            new(Q.PresenceGain, 0, -18, 18, Animatable: false),
            new(Q.PresenceFrequency, 3000, 500, 16000, Animatable: false),
            new(Q.PresenceQ, 1, 0.1, 10, Animatable: false),
            new(Q.HighGain, 0, -18, 18, Animatable: false),
            new(Q.HighFrequency, 8000, 1000, 20000, Animatable: false),
        ]),
        new(BuiltInEffects.Compressor, 21, EffectTarget.Audio, EffectStage.Audio,
        [
            new(K.Threshold, -18, -60, 0),
            new(K.Ratio, 3, 1, 100),
            new(K.Attack, 10, 0.05, 200, Animatable: false),
            new(K.Release, 150, 10, 2000, Animatable: false),
            new(K.Makeup, 0, 0, 24, Animatable: false),
            new(K.Knee, 6, 0, 24, Animatable: false),
            new(K.Mix, 100, 0, 100),
            new(K.Lookahead, 0, 0, 10, Animatable: false),
            new(K.Detector, 0, 0, 1, Animatable: false),
            new(K.RmsWindow, 10, 1, 300, Animatable: false),
            new(K.AutoRelease, 0, 0, 1, Animatable: false),
            new(K.AutoMakeup, 0, 0, 1, Animatable: false),
            new(K.StereoLink, 1, 0, 1, Animatable: false),
            new(K.SidechainHighPass, 0, 0, 500, Animatable: false),
            new(K.Bands, 1, 1, 3, Animatable: false),
            new(K.LowCrossover, 200, 40, 1000, Animatable: false),
            new(K.HighCrossover, 3000, 1000, 12000, Animatable: false),
            new(K.LowThreshold, 0, -24, 24, Animatable: false),
            new(K.MidThreshold, 0, -24, 24, Animatable: false),
            new(K.HighThreshold, 0, -24, 24, Animatable: false),
            new(K.LowGain, 0, -12, 12, Animatable: false),
            new(K.MidGain, 0, -12, 12, Animatable: false),
            new(K.HighGain, 0, -12, 12, Animatable: false),
            new(K.Limiter, 0, 0, 1, Animatable: false),
            new(K.Ceiling, -1, -12, 0, Animatable: false),
        ],
        [OptionDefinition.Text(BuiltInEffects.CompressorOptions.Sidechain)]),
        .. AudioEffectDefinitions.Inserts,

        new(BuiltInTransitions.Crossfade, 101, EffectTarget.Transition, EffectStage.Transition),
        new(BuiltInTransitions.Dip, 102, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.DipParameters.Red, 0, 0, 1, Animatable: false),
            new(BuiltInTransitions.DipParameters.Green, 0, 0, 1, Animatable: false),
            new(BuiltInTransitions.DipParameters.Blue, 0, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Wipe, 103, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.WipeParameters.Angle, 0, -360, 360, Animatable: false),
            new(BuiltInTransitions.WipeParameters.Softness, 0.05, 0, 1, Animatable: false),
            .. Border(),
        ]),
        new(BuiltInTransitions.Push, 104, EffectTarget.Transition, EffectStage.Transition,
        [
            new(TP.MotionBlur, 0, 0, 1, Animatable: false),
        ],
        [OptionDefinition.Choice(BuiltInTransitions.PushOptions.Direction, "left", [.. PushDirections])]),
        new(BuiltInTransitions.ZoomDissolve, 105, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.ZoomDissolveParameters.Zoom, 0.25, 0, 4, Animatable: false),
        ]),
        new(BuiltInTransitions.BlurDissolve, 106, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.BlurDissolveParameters.Radius, 40, 0, 500, Animatable: false),
        ]),
        new(BuiltInTransitions.Preset, 107, EffectTarget.Transition, EffectStage.Transition, [],
        [OptionDefinition.Reference(BuiltInTransitions.PresetOptions.Preset)]),
        new(BuiltInTransitions.Slide, 108, EffectTarget.Transition, EffectStage.Transition,
        [
            new(TP.Angle, 180, -360, 360, Animatable: false),
            new(BuiltInTransitions.SlideParameters.Parallax, 0.3, 0, 1, Animatable: false),
            new(TP.MotionBlur, 0.5, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.ZoomBlur, 109, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.ZoomBlurParameters.Strength, 0.6, 0, 2, Animatable: false),
            new(BuiltInTransitions.ZoomBlurParameters.CenterX, 0.5, 0, 1, Animatable: false),
            new(BuiltInTransitions.ZoomBlurParameters.CenterY, 0.5, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Spin, 110, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.SpinParameters.Angle, 360, -1440, 1440, Animatable: false),
            new(BuiltInTransitions.SpinParameters.Zoom, 0.3, 0, 2, Animatable: false),
            new(TP.MotionBlur, 0.6, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.LightWipe, 111, EffectTarget.Transition, EffectStage.Transition,
        [
            new(TP.Angle, 0, -360, 360, Animatable: false),
            new(TP.Softness, 0.08, 0, 1, Animatable: false),
            new(BuiltInTransitions.LightWipeParameters.Width, 0.12, 0.005, 0.5, Animatable: false),
            new(TP.Intensity, 1.5, 0, 4, Animatable: false),
            new(TP.Red, 1, 0, 1, Animatable: false),
            new(TP.Green, 0.82, 0, 1, Animatable: false),
            new(TP.Blue, 0.55, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Glitch, 112, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.GlitchParameters.Amount, 0.7, 0, 1, Animatable: false),
            new(BuiltInTransitions.GlitchParameters.Bands, 18, 2, 64, Animatable: false),
            new(BuiltInTransitions.GlitchParameters.Split, 0.02, 0, 0.1, Animatable: false),
            new(TP.Seed, 0, 0, 1000, Animatable: false),
        ]),
        new(BuiltInTransitions.FilmBurn, 113, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.FilmBurnParameters.Scale, 0.35, 0.05, 2, Animatable: false),
            new(TP.Red, 1, 0, 1, Animatable: false),
            new(TP.Green, 0.45, 0, 1, Animatable: false),
            new(TP.Blue, 0.12, 0, 1, Animatable: false),
            new(TP.Seed, 0, 0, 1000, Animatable: false),
        ]),
        new(BuiltInTransitions.Flash, 114, EffectTarget.Transition, EffectStage.Transition,
        [
            new(TP.Intensity, 1, 0, 4, Animatable: false),
            new(BuiltInTransitions.FlashParameters.Width, 0.4, 0.05, 1, Animatable: false),
            new(TP.Red, 1, 0, 1, Animatable: false),
            new(TP.Green, 1, 0, 1, Animatable: false),
            new(TP.Blue, 1, 0, 1, Animatable: false),
        ]),
        new(BuiltInTransitions.Luma, 115, EffectTarget.Transition, EffectStage.Transition,
        [
            new(TP.Angle, 0, -360, 360, Animatable: false),
            new(TP.Softness, 0.1, 0, 1, Animatable: false),
            .. Border(),
        ],
        [
            OptionDefinition.Choice(BuiltInTransitions.LumaOptions.Gradient, "linear", [.. LumaGradients]),
            OptionDefinition.Choice(BuiltInTransitions.LumaOptions.Invert, "off", "off", "on"),
            OptionDefinition.Reference(BuiltInTransitions.LumaOptions.Image, allowsFile: true),
        ]),
        new(BuiltInTransitions.Bounce, 116, EffectTarget.Transition, EffectStage.Transition,
        [
            new(BuiltInTransitions.BounceParameters.Bounce, 0.5, 0, 1, Animatable: false),
            new(TP.MotionBlur, 0.4, 0, 1, Animatable: false),
        ],
        [OptionDefinition.Choice(BuiltInTransitions.PushOptions.Direction, "left", [.. PushDirections])]),

        // Las de audio las aplica el mezclador: en una pista de video no van.
        new(BuiltInTransitions.AudioPower, 120, EffectTarget.Transition, EffectStage.Audio),
        new(BuiltInTransitions.AudioGain, 121, EffectTarget.Transition, EffectStage.Audio),
        new(BuiltInTransitions.AudioExponential, 122, EffectTarget.Transition, EffectStage.Audio),
    ];

    private static readonly FrozenDictionary<string, EffectDefinition> ByType =
        All.ToFrozenDictionary(d => d.Type, StringComparer.Ordinal);

    private static readonly FrozenDictionary<int, EffectDefinition> ByCode = All.ToFrozenDictionary(d => d.Code);

    /// <summary>Las transiciones, en el orden en que se ofrecen.</summary>
    public static ImmutableArray<EffectDefinition> Transitions { get; } = [.. All.Where(d => d.IsTransition)];

    public static EffectDefinition? Find(string type) =>
        type is not null && ByType.TryGetValue(type, out EffectDefinition? definition) ? definition : null;

    public static EffectDefinition? Find(int code) => ByCode.TryGetValue(code, out EffectDefinition? definition) ? definition : null;

    public static bool IsKnown(string type) => Find(type) is not null;

    /// <summary>Valor de un parámetro que falta; 0 si el tipo o el parámetro no están registrados.</summary>
    public static double DefaultValue(string type, string parameter) =>
        Find(type)?.FindParameter(parameter)?.DefaultValue ?? 0;

    /// <summary>Valor de una opción que falta; vacío si el tipo o la opción no están registrados.</summary>
    public static string DefaultOption(string type, string option) =>
        Find(type)?.FindOption(option)?.DefaultValue ?? "";
}
