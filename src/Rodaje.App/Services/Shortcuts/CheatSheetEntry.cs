using System.Text;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una línea de la hoja rápida.</summary>
public sealed record CheatSheetEntry(string ActionId, string Label, string Group, IReadOnlyList<string> Keys);
