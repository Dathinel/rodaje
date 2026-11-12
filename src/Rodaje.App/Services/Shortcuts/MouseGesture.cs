namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una acción que solo existe con el ratón (zoom con la rueda, arrastrar la vista).</summary>
public sealed record MouseGesture(string Id, string Label, string ShortLabel, MouseGestureKind Kind, string Description, params MouseContext[] Contexts)
{
    public bool WorksIn(MouseContext context) => Contexts.Contains(context);
}
