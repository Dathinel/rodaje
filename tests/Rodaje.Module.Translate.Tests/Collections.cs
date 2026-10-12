namespace Rodaje.Module.Translate.Tests;

/// <summary>Las pruebas que cargan Qwen3-4B van de una en una: dos copias del modelo no caben en 4 GB de VRAM.</summary>
[CollectionDefinition("Qwen", DisableParallelization = true)]
public sealed class QwenGroup;
