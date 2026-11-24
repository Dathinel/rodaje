namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una acción que se puede elegir para una combinación, en la lista con búsqueda.</summary>
public sealed record BindingChoice(string Id, string Label, string Group, string Description);
