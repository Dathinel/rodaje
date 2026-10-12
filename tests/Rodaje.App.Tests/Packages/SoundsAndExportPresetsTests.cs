using System.Buffers.Binary;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Export.Encoders;
using Rodaje.Media.Export;
using Rodaje.Packages;
using Xunit;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// Lo que traen los paquetes además de efectos: sonidos (se copian al proyecto y entran como clip de audio) y preajustes de
/// exportación (aparecen en el diálogo entre los de fábrica y los propios).
/// </summary>
public sealed class SoundsAndExportPresetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-sonidos", "carpeta ñ " + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void A_sound_is_copied_next_to_the_project_and_placed_on_a_free_audio_track() => UiThread.Run(async () =>
    {
        PackageScan scan = Install();
        PackageSound sound = SoundDrag.Resolve(scan, "rodaje-sonidos-prueba@1.0.0/golpe", out string? problem)!;
        Assert.Null(problem);
        Assert.Equal(("Golpe", 48_000, 2, 24_000L), (sound.Name, sound.Info.SampleRate, sound.Info.Channels, sound.Info.Frames));
        Assert.Equal(Time.FromSeconds(0.5), sound.Duration);

        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Track busy = session.Sequence.AudioTracks[0];
        string media = Path.Combine(_root, "Proyecto.medios");
        string copy = SoundDrag.CopyInto(sound, media);
        Assert.Equal(Path.Combine(media, "sonidos", "rodaje-sonidos-prueba-golpe.wav"), copy);
        Assert.Equal(new FileInfo(sound.FilePath).Length, new FileInfo(copy).Length);
        Assert.Equal(copy, SoundDrag.CopyInto(sound, media));

        // Bajo el puntero está la pista del video, ocupada en ese tramo: va a la primera pista de audio libre.
        Time at = Time.FromSeconds(2);
        Track? free = SoundDrag.TargetTrack(session.Sequence, busy, at, sound.Duration);
        Assert.NotEqual(busy, free);
        int steps = session.History.Count;
        Assert.True(session.InsertSound(copy, sound, at, free?.Id));
        Clip clip = session.Sequence.GetClip(session.SelectedClipIds[0]).Clip;
        Assert.Equal(free?.Id ?? session.Sequence.AudioTracks[^1].Id, session.Sequence.GetClip(clip.Id).Track.Id);
        Assert.Equal((at, Time.FromSeconds(2.5)), (clip.Start, clip.End));
        Assert.Equal(copy, session.Project.FindMedia(clip.MediaId)!.Path);
        Assert.Equal(steps + 1, session.History.Count);

        // El mismo sonido otra vez sobre la pista ocupada: reutiliza el medio y abre una pista «Efectos».
        int mediaCount = session.Project.Media.Count;
        int tracks = session.Sequence.AudioTracks.Count;
        Assert.True(session.InsertSound(copy, sound, Time.FromSeconds(4), busy.Id));
        Assert.Equal(mediaCount, session.Project.Media.Count);
        Assert.Equal(tracks + 1, session.Sequence.AudioTracks.Count);
        Assert.Equal(EditorSession.SoundsTrackName, session.Sequence.GetClip(session.SelectedClipIds[0]).Track.Name);

        session.History.Undo();
        session.History.Undo();
        Assert.DoesNotContain(session.Project.Media, m => m.Path == copy);
    });

    [Fact]
    public void A_missing_or_damaged_sound_says_why()
    {
        PackageScan scan = Install();
        Assert.Null(SoundDrag.Resolve(scan, "otro-paquete@1.0.0/golpe", out string? missing));
        Assert.Contains("otro-paquete", missing, StringComparison.Ordinal);
        Assert.Null(SoundDrag.Resolve(scan, "rodaje-sonidos-prueba@1.0.0/roto", out string? damaged));
        Assert.Contains("Roto", damaged, StringComparison.Ordinal);
        Assert.Null(SoundDrag.Resolve(scan, "no es una referencia", out string? invalid));
        Assert.NotNull(invalid);
    }

    [Fact]
    public void Package_export_presets_become_dialog_presets_that_validate()
    {
        PackageScan scan = Install();
        var problems = new List<string>();

        IReadOnlyList<ExportPreset> presets = PackageExportPresets.Load(scan, problems);

        Assert.Equal(["Desconocido: video.codec «xvid» no existe en esta versión de Rodaje: actualiza el programa."], problems);
        ExportPreset broadcast = Assert.Single(presets);
        Assert.Equal(("rodaje-sonidos-prueba@1.0.0/hd-1080i25", ExportPresetOrigin.Package, ExportPresetCategory.Broadcast),
            (broadcast.Id, broadcast.Origin, broadcast.Category));
        Assert.StartsWith("Sonidos de prueba · ", broadcast.Description, StringComparison.Ordinal);
        VideoExportSettings video = broadcast.Settings.Video!;
        Assert.Equal((ExportCodec.ProRes, "hq", 10, ChromaSubsampling.Chroma422, new Rational(25, 1), ExportFieldOrder.TopFirst),
            (video.Codec, video.Profile, video.BitDepth, video.Chroma, video.FrameRate!.Value, video.FieldOrder));
        Assert.Equal(RateControlMode.ProfileDefined, video.RateControl.Mode);
        Assert.Equal((ExportContainer.Mov, false), (broadcast.Settings.Container, broadcast.Settings.FastStart));
        Assert.Equal(new LoudnessTarget(-23, -1), broadcast.Settings.Audio!.Loudness);
        Assert.Equal(ExportSampleFormat.Pcm24, broadcast.Settings.Audio.PcmFormat);
        Assert.Empty(ExportSettingsValidator.Validate(broadcast.Settings, ExportEncoderCatalog.Find("prores_ks")!, ExportEncoderCatalog.Find("pcm_s24le")!));

        IReadOnlyList<ExportPreset> before = ExportPresetLibrary.Packages;
        try
        {
            ExportPresetLibrary.Packages = presets;
            var preferences = new Rodaje.App.Services.Settings.ExportPreferences();
            IReadOnlyList<ExportPreset> all = ExportPresetLibrary.All(preferences);
            Assert.Equal(ExportPresetLibrary.Factory.Count + 1, all.Count);
            Assert.Same(broadcast, ExportPresetLibrary.Find(preferences, broadcast.Id));
        }
        finally
        {
            ExportPresetLibrary.Packages = before;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PackageScan Install()
    {
        string package = Path.Combine(_root, "p", "rodaje-sonidos-prueba");
        Directory.CreateDirectory(Path.Combine(package, "sonidos"));
        Directory.CreateDirectory(Path.Combine(package, "preajustes"));
        File.WriteAllBytes(Path.Combine(package, "sonidos", "golpe.wav"), Wave(48_000, 2, 24_000));
        File.WriteAllBytes(Path.Combine(package, "sonidos", "roto.wav"), [.. "RIFF"u8, 1, 2, 3]);
        File.WriteAllText(Path.Combine(package, "preajustes", "hd.json"), """
            { "format": "rodaje.exportpreset", "version": 1, "name": "HD 1080i25", "category": "broadcast", "container": "mov", "fastStart": false,
              "video": { "codec": "prores", "profile": "hq", "bitDepth": 10, "chroma": "422", "width": 1920, "height": 1080, "frameRate": 25,
                         "fieldOrder": "tff", "rateControl": "profile" },
              "audio": { "codec": "pcm", "rateControl": "lossless", "loudness": { "lufs": -23 } } }
            """);
        File.WriteAllText(Path.Combine(package, "preajustes", "xvid.json"), """
            { "format": "rodaje.exportpreset", "version": 1, "name": "Xvid", "container": "avi", "video": { "codec": "xvid" } }
            """);
        File.WriteAllText(Path.Combine(package, "paquete.json"), """
            { "formatVersion": 1, "id": "rodaje-sonidos-prueba", "version": "1.0.0", "name": "Sonidos de prueba", "author": "Dathinel",
              "license": "CC0-1.0", "apps": ["rodaje"],
              "items": [
                { "id": "golpe", "kind": "sound", "name": "Golpe", "file": "sonidos/golpe.wav" },
                { "id": "roto", "kind": "sound", "name": "Roto", "file": "sonidos/roto.wav" },
                { "id": "hd-1080i25", "kind": "export-preset", "name": "HD 1080i25", "file": "preajustes/hd.json" },
                { "id": "xvid", "kind": "export-preset", "name": "Desconocido", "file": "preajustes/xvid.json" } ] }
            """);
        return PackageLibrary.Scan(new PackageLocations(Path.Combine(_root, "p"), Path.Combine(_root, "m"), Path.Combine(_root, "propios"), []), "rodaje");
    }

    private static byte[] Wave(int rate, int channels, int frames)
    {
        int data = frames * channels * 2;
        byte[] wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        return wav;
    }
}
