using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Campos del formato 16 que añade la colaboración (D-067): huella de contenido del medio y su escritura.</summary>
public sealed class ContentIdentityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rodaje-contenido-" + Guid.NewGuid().ToString("N"));

    public ContentIdentityTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Project Plain() => ProjectSerializer.Deserialize(Fuzz.ProjectFileMutationFuzzTests.RichProject());

    private string File(int length, int seed = 1)
    {
        string path = Path.Combine(_folder, $"m{length}-{seed}.bin");
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Without_content_identity_nothing_changes_and_the_file_stays_below_16()
    {
        JsonNode root = JsonNode.Parse(ProjectSerializer.Serialize(Plain()))!;

        Assert.True((int)root["schemaVersion"]! < 16);
        Assert.DoesNotContain("contentKey", root.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("contentHash", root.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Each_field_writes_16_and_comes_back(bool key, bool hash)
    {
        Project project = Plain();
        string path = File(3000);
        if (key)
        {
            project.Media[0].ContentKey = ContentKey.Compute(path);
        }

        if (hash)
        {
            project.Media[0].ContentHash = ContentHash.Compute(path);
        }

        string json = ProjectSerializer.Serialize(project);
        Project back = ProjectSerializer.Deserialize(json);

        Assert.Equal(16, (int)JsonNode.Parse(json)!["schemaVersion"]!);
        Assert.Empty(ModelValidator.Validate(back));
        Assert.Equal(project.Media[0].ContentKey, back.Media[0].ContentKey);
        Assert.Equal(project.Media[0].ContentHash, back.Media[0].ContentHash);
        Assert.Equal(json, ProjectSerializer.Serialize(back));
    }

    [Theory]
    [InlineData("contentKey", "abc")]
    [InlineData("contentKey", "12:ZZ")]
    [InlineData("contentKey", "012:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("contentKey", "5:0000000000000000000000000000000000000000000000000000000000000000X")]
    [InlineData("contentHash", "abc")]
    [InlineData("contentHash", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_malformed_identity_is_rejected(string field, string value)
    {
        Project project = Plain();
        if (field == "contentKey")
        {
            project.Media[0].ContentKey = value;
        }
        else
        {
            project.Media[0].ContentHash = value;
        }

        Assert.Contains(ModelValidator.Validate(project), e => e.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void A_small_file_is_summarised_whole_and_the_key_has_the_size()
    {
        string path = File(1000);
        byte[] bytes = System.IO.File.ReadAllBytes(path);

        string key = ContentKey.Compute(path);

        Assert.Equal($"1000:{Convert.ToHexString(SHA256.HashData(bytes))}", key);
        Assert.True(ContentKey.IsValid(key));
    }

    [Fact]
    public void A_big_file_is_summarised_by_its_first_and_last_mebibyte()
    {
        string path = File((5 << 20) + 123);
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        byte[] sample = [.. bytes.AsSpan(0, 1 << 20), .. bytes.AsSpan(bytes.Length - (1 << 20))];

        Assert.Equal($"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(sample))}", ContentKey.Compute(path));

        // Un cambio en el centro no mueve la muestra, pero sí el hash entero: por eso hacen falta los dos.
        string before = ContentKey.Compute(path);
        string hashBefore = ContentHash.Compute(path);
        bytes[2 << 20] ^= 0xFF;
        System.IO.File.WriteAllBytes(path, bytes);

        Assert.Equal(before, ContentKey.Compute(path));
        Assert.NotEqual(hashBefore, ContentHash.Compute(path));
    }

    [Fact]
    public async Task The_async_hash_matches_the_sync_one_and_can_be_cancelled()
    {
        string path = File(70_000);

        Assert.Equal(ContentHash.Compute(path), await ContentHash.ComputeAsync(path));
        Assert.True(ContentHash.IsValid(ContentHash.Compute(path)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContentHash.ComputeAsync(path, cancelled.Token));
    }

    [Fact]
    public void An_empty_file_has_a_valid_key_and_hash()
    {
        string path = File(0);

        Assert.True(ContentKey.IsValid(ContentKey.Compute(path)));
        Assert.True(ContentHash.IsValid(ContentHash.Compute(path)));
        Assert.StartsWith("0:", ContentKey.Compute(path), StringComparison.Ordinal);
    }
}
