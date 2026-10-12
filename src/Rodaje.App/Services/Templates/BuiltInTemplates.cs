using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Templates;

/// <summary>Las plantillas que vienen con Rodaje: los formatos de siempre a 30 fps, con dos pistas de video y dos de audio.</summary>
public static class BuiltInTemplates
{
    public const string Landscape1080Id = ProjectTemplate.BuiltInPrefix + "16x9-1080p";
    public const string Vertical1080Id = ProjectTemplate.BuiltInPrefix + "9x16-1080p";
    public const string Square1080Id = ProjectTemplate.BuiltInPrefix + "1x1-1080";
    public const string Portrait1080Id = ProjectTemplate.BuiltInPrefix + "4x5-1080";
    public const string Landscape4KId = ProjectTemplate.BuiltInPrefix + "16x9-4k";

    /// <summary>En el orden del menú.</summary>
    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        Create(Landscape1080Id, "Horizontal 16:9 (1080p)", 1920, 1080),
        Create(Vertical1080Id, "Vertical 9:16 (videos cortos)", 1080, 1920),
        Create(Square1080Id, "Cuadrado 1:1", 1080, 1080),
        Create(Portrait1080Id, "Retrato 4:5 (publicaciones)", 1080, 1350),
        Create(Landscape4KId, "Horizontal 16:9 (4K)", 3840, 2160),
    ];

    /// <summary>La plantilla de fábrica con ese identificador, o <c>null</c>.</summary>
    public static ProjectTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static ProjectTemplate Create(string id, string name, int width, int height) => new()
    {
        Id = id,
        Name = name,
        Width = width,
        Height = height,
        FrameRate = Rational.Fps30,
        SampleRate = 48000,
        VideoTracks = [new() { Name = "Video 1" }, new() { Name = "Video 2" }],
        AudioTracks = [new() { Name = "Audio 1" }, new() { Name = "Audio 2" }],
    };
}
