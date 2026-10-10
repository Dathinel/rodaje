using System.Text.Json;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;

namespace Rodaje.Media.Tests.Proxies;

public sealed class ProxyStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new(Start);
    private readonly ProxyStore _store;

    public ProxyStoreTests()
    {
        _store = new ProxyStore(_temp.Combine("almacén de proxies"), _clock);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Default_directory_is_local_app_data_unless_overridden()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "proxies");

        Assert.Equal(
            Environment.GetEnvironmentVariable(ProxyStore.DirectoryEnvironmentVariable) is { Length: > 0 } configured ? configured : expected,
            ProxyStore.DefaultDirectory);
    }

    [Fact]
    public void Fingerprint_is_stable_and_depends_on_path_size_date_and_both_ends()
    {
        string source = WriteSource("toma 1.mov", 5 << 20);
        SourceFingerprint first = SourceFingerprint.Compute(source);

        Assert.Equal(first, SourceFingerprint.Compute(source));
        Assert.Equal(32, first.Key.Length);
        Assert.Equal(first.Key, SourceFingerprint.Compute(source.ToUpperInvariant()).Key);

        string copy = _temp.Combine("toma 2.mov");
        File.Copy(source, copy);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(source));
        Assert.NotEqual(first.Key, SourceFingerprint.Compute(copy).Key);

        DateTime date = File.GetLastWriteTimeUtc(source);
        Assert.NotEqual(first.Key, KeyAfter(source, s => Flip(s, 100), date));
        Assert.NotEqual(first.Key, KeyAfter(source, s => Flip(s, s.Length - 100), date));
        Assert.NotEqual(first.Key, KeyAfter(source, s => s.SetLength(s.Length + 1), date));
        Assert.NotEqual(first.Key, KeyAfter(source, _ => { }, date.AddSeconds(2)));
    }

    [Fact]
    public void Fingerprint_of_a_small_file_reads_it_whole()
    {
        string source = WriteSource("corto.mp4", 1000);
        string key = SourceFingerprint.Compute(source).Key;

        DateTime date = File.GetLastWriteTimeUtc(source);
        Assert.NotEqual(key, KeyAfter(source, s => Flip(s, 500), date));
        Assert.Throws<FileNotFoundException>(() => SourceFingerprint.Compute(_temp.Combine("no existe.mp4")));
    }

    [Fact]
    public void Committed_proxy_is_found_with_its_metadata_and_no_temporary_files_remain()
    {
        string source = WriteSource("toma ñ.mp4", 3 << 20);
        SourceFingerprint fingerprint = SourceFingerprint.Compute(source);

        ProxyEntry entry = CommitFake(fingerprint, ProxySettings.Default, bytes: 12345);

        Assert.Equal(_store.ProxyPath(fingerprint, ProxySettings.Default), entry.ProxyPath);
        Assert.EndsWith($"{fingerprint.Key}-h540-g15-v1.mp4", entry.ProxyPath, StringComparison.Ordinal);
        Assert.Equal(12345, entry.Metadata.ProxyBytes);
        Assert.Equal(new[] { entry.MetadataPath, entry.ProxyPath }.Order(), Directory.EnumerateFiles(_store.Directory).Order());
        ProxyEntry? found = _store.TryGet(fingerprint, ProxySettings.Default);
        Assert.NotNull(found);
        Assert.Equal(entry.Metadata, found.Metadata);
        Assert.Null(_store.TryGet(fingerprint, new ProxySettings { TargetHeight = 720 }));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(entry.MetadataPath));
        Assert.Equal(fingerprint.Key, json.RootElement.GetProperty("sourceKey").GetString());
        Assert.Equal(source, json.RootElement.GetProperty("sourcePath").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void A_changed_source_no_longer_finds_its_proxy()
    {
        string source = WriteSource("a.mp4", 3 << 20);
        SourceFingerprint before = SourceFingerprint.Compute(source);
        CommitFake(before, ProxySettings.Default);

        using (var stream = new FileStream(source, FileMode.Open))
        {
            stream.Position = 10;
            stream.WriteByte(0x5A);
        }

        SourceFingerprint after = SourceFingerprint.Compute(source);
        Assert.NotEqual(before.Key, after.Key);
        Assert.Null(_store.TryGet(after, ProxySettings.Default));
        Assert.NotNull(_store.TryGet(before, ProxySettings.Default));
    }

    [Fact]
    public void A_proxy_whose_size_does_not_match_its_metadata_is_not_ready()
    {
        SourceFingerprint fingerprint = SourceFingerprint.Compute(WriteSource("b.mp4", 2048));
        ProxyEntry entry = CommitFake(fingerprint, ProxySettings.Default, bytes: 4096);

        File.WriteAllBytes(entry.ProxyPath, new byte[100]);

        Assert.Null(_store.TryGet(fingerprint, ProxySettings.Default));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Corrupt_metadata_is_not_ready_and_cleanup_removes_it()
    {
        SourceFingerprint fingerprint = SourceFingerprint.Compute(WriteSource("c.mp4", 2048));
        ProxyEntry entry = CommitFake(fingerprint, ProxySettings.Default);
        File.WriteAllText(entry.MetadataPath, "{ esto no es json");

        Assert.Null(_store.TryGet(fingerprint, ProxySettings.Default));
        ProxyCleanupReport report = _store.Cleanup();

        Assert.Equal(2, report.Orphans);
        Assert.Empty(Directory.EnumerateFiles(_store.Directory));
    }

    [Fact]
    public void Committing_again_replaces_the_previous_proxy()
    {
        SourceFingerprint fingerprint = SourceFingerprint.Compute(WriteSource("d.mp4", 2048));
        CommitFake(fingerprint, ProxySettings.Default, bytes: 1000);

        ProxyEntry second = CommitFake(fingerprint, ProxySettings.Default, bytes: 2000, encoder: "h264_mf");

        Assert.Equal(2000, new FileInfo(second.ProxyPath).Length);
        Assert.Equal("h264_mf", _store.TryGet(fingerprint, ProxySettings.Default)!.Metadata.Encoder);
        Assert.Equal(2, Directory.EnumerateFiles(_store.Directory).Count());
    }

    [Fact]
    public void Cleanup_removes_abandoned_temporaries_but_keeps_fresh_ones()
    {
        SourceFingerprint fingerprint = SourceFingerprint.Compute(WriteSource("e.mp4", 2048));
        string old = _store.CreateTemporaryPath(fingerprint, ProxySettings.Default);
        string fresh = _store.CreateTemporaryPath(fingerprint, ProxySettings.Default);
        File.WriteAllBytes(old, new byte[10]);
        File.WriteAllBytes(fresh, new byte[10]);
        _clock.Now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(old, _clock.Now.UtcDateTime.AddHours(-2));

        ProxyCleanupReport report = _store.Cleanup();

        Assert.Equal(1, report.TemporaryFiles);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.Contains(".tmp", fresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Cleanup_removes_orphans_and_proxies_of_missing_or_changed_sources()
    {
        string kept = WriteSource("sigue.mp4", 4096);
        string deleted = WriteSource("borrado.mp4", 4096);
        string changed = WriteSource("cambiado.mp4", 4096);
        ProxyEntry keep = CommitFake(SourceFingerprint.Compute(kept), ProxySettings.Default);
        CommitFake(SourceFingerprint.Compute(deleted), ProxySettings.Default);
        CommitFake(SourceFingerprint.Compute(changed), ProxySettings.Default);
        File.Delete(deleted);
        DateTime date = File.GetLastWriteTimeUtc(changed);
        File.WriteAllBytes(changed, new byte[4096]);
        File.SetLastWriteTimeUtc(changed, date);
        File.WriteAllBytes(Path.Combine(_store.Directory, "huérfano-h540-g15-v1.mp4"), new byte[50]);
        File.WriteAllText(Path.Combine(_store.Directory, "solo-json-h540-g15-v1.json"), "{}");

        ProxyCleanupReport report = _store.Cleanup();

        Assert.Equal(2, report.Stale);
        Assert.Equal(2, report.Orphans);
        Assert.Equal(0, report.InUse);
        Assert.Equal(new[] { keep.MetadataPath, keep.ProxyPath }.Order(), Directory.EnumerateFiles(_store.Directory).Order());
        Assert.Equal(new FileInfo(keep.ProxyPath).Length, report.RemainingBytes);
    }

    [Fact]
    public void Budget_evicts_least_recently_used_first_and_respects_protected_sources()
    {
        var entries = new List<ProxyEntry>();
        for (int i = 0; i < 4; i++)
        {
            _clock.Now = Start.AddMinutes(i);
            entries.Add(CommitFake(SourceFingerprint.Compute(WriteSource($"f{i}.mp4", 2048)), ProxySettings.Default, bytes: 1000));
        }

        _clock.Now = Start.AddMinutes(10);
        Assert.NotNull(_store.TryGet(SourceFingerprint.Compute(_temp.Combine("f0.mp4")), ProxySettings.Default));

        ProxyCleanupReport report = _store.Cleanup(new ProxyCleanupOptions
        {
            MaxTotalBytes = 2000,
            ProtectedSourceKeys = new HashSet<string> { entries[1].Metadata.SourceKey },
        });

        Assert.Equal(2, report.Evicted);
        Assert.Equal(2000, report.RemainingBytes);
        Assert.True(File.Exists(entries[0].ProxyPath), "f0 se usó hace poco.");
        Assert.True(File.Exists(entries[1].ProxyPath), "f1 está protegido.");
        Assert.False(File.Exists(entries[2].ProxyPath));
        Assert.False(File.Exists(entries[3].ProxyPath));
        Assert.False(File.Exists(entries[2].MetadataPath));
    }

    [Fact]
    public void A_proxy_that_is_open_is_not_deleted_and_keeps_its_metadata()
    {
        string source = WriteSource("g.mp4", 2048);
        ProxyEntry entry = CommitFake(SourceFingerprint.Compute(source), ProxySettings.Default);
        File.Delete(source);

        using (new FileStream(entry.ProxyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            ProxyCleanupReport report = _store.Cleanup();

            Assert.Equal(1, report.InUse);
            Assert.Equal(0, report.Stale);
            Assert.True(File.Exists(entry.ProxyPath));
            Assert.True(File.Exists(entry.MetadataPath));
            Assert.False(ProxyStore.Delete(entry));
        }

        Assert.Equal(1, _store.Cleanup().Stale);
        Assert.Empty(Directory.EnumerateFiles(_store.Directory));
    }

    [Fact]
    public void Cleanup_of_a_missing_directory_does_nothing()
    {
        var store = new ProxyStore(_temp.Combine("no existe"));

        Assert.Equal(new ProxyCleanupReport(0, 0, 0, 0, 0, 0, 0), store.Cleanup());
        Assert.Empty(store.List());
        Assert.Equal(0, store.TotalBytes());
    }

    internal static ProxyMetadata FakeMetadata(SourceFingerprint fingerprint, ProxySettings settings, string encoder = "h264_nvenc") => new()
    {
        SourcePath = fingerprint.Path,
        SourceKey = fingerprint.Key,
        SourceSize = fingerprint.Size,
        SourceLastWriteUtc = fingerprint.LastWriteUtc,
        SettingsTag = settings.Tag,
        TargetHeight = settings.TargetHeight,
        GopLength = settings.GopLength,
        Encoder = encoder,
        Width = 960,
        Height = 540,
        FrameRateNum = 30000,
        FrameRateDen = 1001,
        FrameCount = 599,
        SourceFrameCount = 599,
        SourceStartTicks = 0,
        ProxyStartTicks = 0,
        SourceDurationTicks = 1,
        ProxyDurationTicks = 1,
        CreatedUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
    };

    private ProxyEntry CommitFake(SourceFingerprint fingerprint, ProxySettings settings, int bytes = 777, string encoder = "h264_nvenc")
    {
        string temporary = _store.CreateTemporaryPath(fingerprint, settings);
        File.WriteAllBytes(temporary, new byte[bytes]);
        ProxyEntry entry = _store.Commit(temporary, FakeMetadata(fingerprint, settings, encoder), settings);
        Assert.False(File.Exists(temporary));
        return entry;
    }

    private string WriteSource(string name, int size)
    {
        string path = _temp.Combine(name);
        var bytes = new byte[size];
        new Random(name.GetHashCode(StringComparison.Ordinal)).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Flip(FileStream stream, long position)
    {
        stream.Position = position;
        int value = stream.ReadByte();
        stream.Position = position;
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    private static string KeyAfter(string path, Action<FileStream> change, DateTime lastWrite)
    {
        byte[] original = File.ReadAllBytes(path);
        try
        {
            using (var stream = new FileStream(path, FileMode.Open))
            {
                change(stream);
            }

            File.SetLastWriteTimeUtc(path, lastWrite);
            return SourceFingerprint.Compute(path).Key;
        }
        finally
        {
            File.WriteAllBytes(path, original);
            File.SetLastWriteTimeUtc(path, lastWrite);
        }
    }
}
