using System.Text;

namespace Rodaje.ShaderTool;

/// <summary>Lectura mínima de un módulo SPIR-V: variables con binding, tipo y tamaño de uniform buffers.</summary>
internal static class SpirvReflect
{
    private const int OpName = 5, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23, OpTypeMatrix = 24, OpTypeImage = 25,
        OpTypeSampler = 26, OpTypeSampledImage = 27, OpTypeArray = 28, OpTypeRuntimeArray = 29, OpTypeStruct = 30, OpTypePointer = 32, OpConstant = 43,
        OpVariable = 59, OpDecorate = 71, OpMemberDecorate = 72;

    private const int DecorationBlock = 2, DecorationBufferBlock = 3, DecorationArrayStride = 6,
        DecorationBinding = 33, DecorationDescriptorSet = 34, DecorationOffset = 35;

    public sealed record Resource(string Name, int Set, int Binding, string Kind, string? Dimension, int? Size);

    public static List<Resource> Read(byte[] module)
    {
        var w = new uint[module.Length / 4];
        Buffer.BlockCopy(module, 0, w, 0, w.Length * 4);
        if (w.Length < 5 || w[0] != 0x07230203)
        {
            throw new InvalidDataException("No es un módulo SPIR-V.");
        }

        var names = new Dictionary<uint, string>();
        var binding = new Dictionary<uint, int>();
        var set = new Dictionary<uint, int>();
        var arrayStride = new Dictionary<uint, uint>();
        var memberOffset = new Dictionary<(uint, uint), uint>();
        var types = new Dictionary<uint, uint[]>();
        var constants = new Dictionary<uint, uint>();
        var blockDecorations = new HashSet<uint>();
        var bufferBlockDecorations = new HashSet<uint>();
        var variables = new List<(uint Id, uint Type, uint Storage)>();

        for (int i = 5; i < w.Length;)
        {
            int count = (int)(w[i] >> 16);
            int op = (int)(w[i] & 0xFFFF);
            var operands = w.AsSpan(i + 1, count - 1);
            switch (op)
            {
                case OpName:
                    names[operands[0]] = LiteralString(operands[1..]);
                    break;
                case OpDecorate when operands[1] == DecorationBinding:
                    binding[operands[0]] = (int)operands[2];
                    break;
                case OpDecorate when operands[1] == DecorationDescriptorSet:
                    set[operands[0]] = (int)operands[2];
                    break;
                case OpDecorate when operands[1] == DecorationArrayStride:
                    arrayStride[operands[0]] = operands[2];
                    break;
                case OpDecorate when operands[1] == DecorationBlock:
                    blockDecorations.Add(operands[0]);
                    break;
                case OpDecorate when operands[1] == DecorationBufferBlock:
                    bufferBlockDecorations.Add(operands[0]);
                    break;
                case OpMemberDecorate when operands[2] == DecorationOffset:
                    memberOffset[(operands[0], operands[1])] = operands[3];
                    break;
                case OpTypeInt or OpTypeFloat or OpTypeVector or OpTypeMatrix or OpTypeImage or OpTypeSampler or OpTypeSampledImage
                    or OpTypeArray or OpTypeRuntimeArray or OpTypeStruct or OpTypePointer:
                    types[operands[0]] = [(uint)op, .. operands[1..]];
                    break;
                case OpConstant:
                    constants[operands[1]] = operands[2];
                    break;
                case OpVariable:
                    variables.Add((operands[1], operands[0], operands[2]));
                    break;
            }

            i += count;
        }

        int Size(uint type)
        {
            uint[] t = types[type];
            return (int)t[0] switch
            {
                OpTypeInt or OpTypeFloat => (int)t[1] / 8,
                OpTypeVector => (int)t[2] * Size(t[1]),
                OpTypeArray => (int)(constants[t[2]] * arrayStride[type]),
                OpTypeStruct => Enumerable.Range(0, t.Length - 1).Max(m => (int)memberOffset[(type, (uint)m)] + Size(t[m + 1])),
                _ => throw new InvalidDataException($"Tipo SPIR-V {t[0]} sin tamaño conocido."),
            };
        }

        var result = new List<Resource>();
        foreach (var (id, pointer, storage) in variables)
        {
            if (!binding.TryGetValue(id, out int b))
            {
                continue;
            }

            uint pointee = types[pointer][2];
            while (types[pointee][0] == OpTypeArray)
            {
                pointee = types[pointee][1];
            }

            uint[] t = types[pointee];
            string name = names.GetValueOrDefault(id) ?? names.GetValueOrDefault(pointee) ?? $"%{id}";
            int s = set.GetValueOrDefault(id);

            bool isBufferBlock = bufferBlockDecorations.Contains(pointee) || storage == 12;
            bool isUniformBlock = blockDecorations.Contains(pointee) && storage == 2 && !isBufferBlock;

            Resource r = (int)t[0] switch
            {
                OpTypeStruct when isBufferBlock => new(name, s, b, "storage-buffer", null, null),
                OpTypeStruct when isUniformBlock || storage == 2 => new(name, s, b, "uniform-buffer", null, Size(pointee)),
                OpTypeSampler => new(name, s, b, "sampler", null, null),
                OpTypeImage => new(name, s, b, t[6] == 2 ? "storage-image" : "sampled-image",
                    t[2] switch { 1 when t[4] == 1 => "2d-array", 1 => "2d", 2 => "3d", _ => $"dim{t[2]}" }, null),
                _ => new(name, s, b, $"op{t[0]}", null, null),
            };
            result.Add(r);
        }

        return [.. result.OrderBy(r => r.Set).ThenBy(r => r.Binding)];
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
