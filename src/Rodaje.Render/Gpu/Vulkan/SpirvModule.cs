using System.Text;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>Qué descriptor pide una variable de un módulo SPIR-V.</summary>
internal enum SpirvDescriptor
{
    UniformBuffer,
    StorageBuffer,
    SampledImage,
    StorageImage,
    Sampler,
    UniformTexelBuffer,
    StorageTexelBuffer,
}

/// <summary>Dimensión de una imagen enlazada: DXC declara <c>Texture2D</c>, <c>Texture2DArray</c> (los planos) o <c>Texture3D</c>.</summary>
internal enum SpirvDimension
{
    None,
    D2,
    D2Array,
    D3,
}

internal readonly record struct SpirvBinding(int Binding, SpirvDescriptor Kind, SpirvDimension Dimension);

/// <summary>
/// Lo que el motor necesita de un módulo de DXC: el punto de entrada y los enlaces del conjunto 0 (registros con los desplazamientos del
/// manifiesto: <c>b</c> +0, <c>t</c> +16, <c>s</c> +32, <c>u</c> +48). Lectura mínima, sin dependencias.
/// </summary>
internal sealed class SpirvModule
{
    private const int OpEntryPoint = 15, OpTypeImage = 25, OpTypeSampler = 26, OpTypeSampledImage = 27, OpTypeArray = 28,
        OpTypeRuntimeArray = 29, OpTypeStruct = 30, OpTypePointer = 32, OpVariable = 59, OpDecorate = 71;

    private const int DecorationBlock = 2, DecorationBufferBlock = 3, DecorationBinding = 33;
    private const uint StorageUniformConstant = 0, StorageUniform = 2, StorageStorageBuffer = 12;

    private SpirvModule(byte[] code, string entryPoint, int executionModel, SpirvBinding[] bindings)
    {
        Code = code;
        EntryPoint = entryPoint;
        ExecutionModel = executionModel;
        Bindings = bindings;
    }

    public byte[] Code { get; }

    public string EntryPoint { get; }

    /// <summary>0 vértices, 4 fragmentos, 5 cómputo.</summary>
    public int ExecutionModel { get; }

    public SpirvBinding[] Bindings { get; }

    public static SpirvModule Read(byte[] code)
    {
        if (code.Length < 20 || code.Length % 4 != 0 || BitConverter.ToUInt32(code, 0) != 0x07230203)
        {
            throw new RenderException("No es un módulo SPIR-V.");
        }

        uint[] w = new uint[code.Length / 4];
        Buffer.BlockCopy(code, 0, w, 0, code.Length);
        var binding = new Dictionary<uint, int>();
        var blocks = new HashSet<uint>();
        var bufferBlocks = new HashSet<uint>();
        var types = new Dictionary<uint, uint[]>();
        var variables = new List<(uint Id, uint Type, uint Storage)>();
        string? entry = null;
        int model = -1;
        for (int i = 5; i < w.Length;)
        {
            int count = (int)(w[i] >> 16);
            int op = (int)(w[i] & 0xFFFF);
            if (count == 0 || i + count > w.Length)
            {
                throw new RenderException("Módulo SPIR-V truncado.");
            }

            ReadOnlySpan<uint> o = w.AsSpan(i + 1, count - 1);
            switch (op)
            {
                case OpEntryPoint when entry is null:
                    model = (int)o[0];
                    entry = LiteralString(o[2..]);
                    break;
                case OpDecorate when o[1] == DecorationBinding:
                    binding[o[0]] = (int)o[2];
                    break;
                case OpDecorate when o[1] == DecorationBlock:
                    blocks.Add(o[0]);
                    break;
                case OpDecorate when o[1] == DecorationBufferBlock:
                    bufferBlocks.Add(o[0]);
                    break;
                case OpTypeImage or OpTypeSampler or OpTypeSampledImage or OpTypeArray or OpTypeRuntimeArray or OpTypeStruct or OpTypePointer:
                    types[o[0]] = [(uint)op, .. o[1..]];
                    break;
                case OpVariable:
                    variables.Add((o[1], o[0], o[2]));
                    break;
            }

            i += count;
        }

        var bindings = new List<SpirvBinding>();
        foreach ((uint id, uint pointer, uint storage) in variables)
        {
            if (!binding.TryGetValue(id, out int b) || storage is not (StorageUniformConstant or StorageUniform or StorageStorageBuffer))
            {
                continue;
            }

            uint pointee = types[pointer][2];
            while (types[pointee][0] is OpTypeArray or OpTypeRuntimeArray)
            {
                pointee = types[pointee][1];
            }

            uint[] t = types[pointee];
            if (t[0] == OpTypeSampledImage)
            {
                t = types[t[1]];
            }

            SpirvBinding found = (int)t[0] switch
            {
                OpTypeStruct when storage == StorageStorageBuffer || bufferBlocks.Contains(pointee) => new(b, SpirvDescriptor.StorageBuffer, SpirvDimension.None),
                OpTypeStruct when blocks.Contains(pointee) || storage == StorageUniform => new(b, SpirvDescriptor.UniformBuffer, SpirvDimension.None),
                OpTypeSampler => new(b, SpirvDescriptor.Sampler, SpirvDimension.None),
                // OpTypeImage: tipo muestreado, Dim, Depth, Arrayed, MS, Sampled (1 lectura, 2 escritura).
                OpTypeImage when t[2] == 5 => new(b, t[6] == 2 ? SpirvDescriptor.StorageTexelBuffer : SpirvDescriptor.UniformTexelBuffer, SpirvDimension.None),
                OpTypeImage => new(b, t[6] == 2 ? SpirvDescriptor.StorageImage : SpirvDescriptor.SampledImage,
                    t[2] == 2 ? SpirvDimension.D3 : t[4] == 1 ? SpirvDimension.D2Array : SpirvDimension.D2),
                _ => throw new RenderException($"Enlace {b} de tipo SPIR-V {t[0]} sin equivalente."),
            };
            bindings.Add(found);
        }

        if (entry is null)
        {
            throw new RenderException("El módulo SPIR-V no tiene punto de entrada.");
        }

        return new SpirvModule(code, entry, model, [.. bindings.OrderBy(x => x.Binding)]);
    }

    private static string LiteralString(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint word in words)
        {
            for (int k = 0; k < 4; k++)
            {
                byte c = (byte)(word >> (8 * k));
                if (c == 0)
                {
                    return Encoding.UTF8.GetString([.. bytes]);
                }

                bytes.Add(c);
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
