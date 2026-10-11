namespace Rodaje.App.Services.Templates;

/// <summary>Una pista de una plantilla: su nombre y, si la tiene, su función (<see cref="Rodaje.Core.Model.TrackRoles"/>).</summary>
public sealed class TemplateTrack
{
    public string Name { get; set; } = "";

    /// <summary>Función de la pista; nula en una pista corriente.</summary>
    public string? Role { get; set; }
}
