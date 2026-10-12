namespace Rodaje.Module.Translate.Tests;

/// <summary>Necesita el modelo del traductor en <c>.entorno\modelos</c> (o <c>RODAJE_MODELOS</c>); si no está, se omite.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModelFactAttribute : FactAttribute
{
    public static readonly string? ModelPath = ModelFiles.Search();

    public ModelFactAttribute()
    {
        if (ModelPath is null)
        {
            Skip = $"Omitida: falta el modelo {ModelFiles.ModelFile} en .entorno\\modelos; ejecuta herramientas\\preparar-modelos.ps1 -Traductor.";
        }
    }
}
