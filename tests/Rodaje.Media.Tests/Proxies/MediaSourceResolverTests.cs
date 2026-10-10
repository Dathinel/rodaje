using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>El resolvedor sin generador: solo usa proxies que ya están en el almacén.</summary>
public sealed class MediaSourceResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ProxyStore _store;
    private readonly string _source;

    public MediaSourceResolverTests()
    {
        _store = new ProxyStore(_temp.Combine("proxies"));
        _source = _temp.Combine("medios\\toma 4K ñ.mov");
        Directory.CreateDirectory(Path.GetDirectoryName(_source)!);
        File.WriteAllBytes(_source, Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray());
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Without_a_proxy_preview_and_export_use_the_original()
    {
        using var resolver = new MediaSourceResolver(_store, generator: null);

        MediaSourceResolution preview = resolver.Resolve(_source, MediaPurpose.Preview);
        MediaSourceResolution export = resolver.Resolve(_source, MediaPurpose.Export);

        Assert.False(preview.IsProxy);
        Assert.Equal(_source, preview.VideoPath);
        Assert.Equal(_source, preview.AudioPath);
        Assert.Null(preview.Proxy);
        Assert.Equal(ProxyState.Unknown, preview.Status.State);
        Assert.Equal(_source, export.VideoPath);
    }

    [Fact]
    public void A_ready_proxy_is_used_for_preview_only_and_audio_stays_on_the_original()
    {
        ProxyEntry entry = CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null);

        MediaSourceResolution preview = resolver.Resolve(_source, MediaPurpose.Preview);
        MediaSourceResolution export = resolver.Resolve(_source, MediaPurpose.Export);

        Assert.True(preview.IsProxy);
        Assert.Equal(entry.ProxyPath, preview.VideoPath);
        Assert.Equal(_source, preview.AudioPath);
        Assert.Equal(ProxyState.Ready, preview.Status.State);
        Assert.Equal(1, preview.Status.Progress);
        Assert.Equal(960, preview.Proxy!.Metadata.Width);
        Assert.False(export.IsProxy);
        Assert.Equal(_source, export.VideoPath);
        Assert.Equal(ProxyState.Ready, export.Status.State);
    }

    [Fact]
    public void Media_refs_resolve_relative_to_the_project_folder()
    {
        ProxyEntry entry = CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null);
        var media = new MediaRef { Id = "m1", Path = @"medios\toma 4K ñ.mov", Duration = Time.FromSeconds(10), HasVideo = true };

        Assert.Equal(entry.ProxyPath, resolver.Resolve(media, _temp.Path, MediaPurpose.Preview).VideoPath);
        Assert.Equal(_source, resolver.Resolve(media, _temp.Path, MediaPurpose.Export).VideoPath);
    }

    [Fact]
    public void Turning_proxies_off_returns_the_original_for_preview()
    {
        CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null) { PreviewUsesProxies = false };

        MediaSourceResolution preview = resolver.Resolve(_source, MediaPurpose.Preview);

        Assert.False(preview.IsProxy);
        Assert.Equal(ProxyState.Ready, preview.Status.State);
    }

    [Fact]
    public void When_the_original_changes_its_proxy_stops_being_used()
    {
        CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null);
        var events = new List<ProxyStatus>();
        resolver.StatusChanged += (_, e) => events.Add(e.Status);
        Assert.True(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);

        File.WriteAllBytes(_source, new byte[1000]);

        MediaSourceResolution after = resolver.Resolve(_source, MediaPurpose.Preview);
        Assert.False(after.IsProxy);
        Assert.Equal(ProxyState.Unknown, after.Status.State);
        Assert.Null(after.Status.Proxy);
        Assert.Equal([ProxyState.Ready, ProxyState.Unknown], events.Select(e => e.State));
    }

    [Fact]
    public void A_deleted_proxy_file_falls_back_to_the_original()
    {
        ProxyEntry entry = CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null);
        Assert.True(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);

        File.Delete(entry.ProxyPath);

        Assert.False(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);
    }

    [Fact]
    public void A_proxy_that_appears_later_is_found()
    {
        using var resolver = new MediaSourceResolver(_store, generator: null);
        Assert.False(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);

        CommitFake();

        Assert.True(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);
    }

    [Fact]
    public void Assess_records_the_recommendation_and_reports_unavailable_without_a_generator()
    {
        using var resolver = new MediaSourceResolver(_store, generator: null, unavailableReason: "Falta ffmpeg.exe.");
        var events = new List<ProxyStatus>();
        resolver.StatusChanged += (_, e) => events.Add(e.Status);

        ProxyRecommendation heavy = resolver.Assess(_source, new ProxyCandidate(3840, 2160, "hevc", 10, Rational.Fps30));
        ProxyStatus status = resolver.GetStatus(_source);

        Assert.True(heavy.IsRecommended);
        Assert.Equal(ProxyState.Unavailable, status.State);
        Assert.Equal("Falta ffmpeg.exe.", status.Message);
        Assert.Same(heavy, status.Recommendation);
        Assert.Equal(ProxyState.Unavailable, events[^1].State);
        Assert.False(resolver.CanGenerate);
        Assert.Throws<InvalidOperationException>(() => { _ = resolver.RequestProxyAsync(_source); });
    }

    [Fact]
    public void Assess_of_a_light_clip_says_no_proxy_is_needed()
    {
        using var resolver = new MediaSourceResolver(_store, generator: null);

        ProxyRecommendation light = resolver.Assess(ProxyPolicyTests.Info() with { Path = _source });

        Assert.False(light.IsRecommended);
        Assert.Equal(ProxyState.NotNeeded, resolver.GetStatus(_source).State);
        Assert.Single(resolver.Snapshot());
    }

    [Fact]
    public void Assess_does_not_hide_a_ready_proxy()
    {
        CommitFake();
        using var resolver = new MediaSourceResolver(_store, generator: null);

        resolver.Assess(_source, new ProxyCandidate(3840, 2160, "hevc", 10, Rational.Fps30));

        Assert.Equal(ProxyState.Ready, resolver.GetStatus(_source).State);
        Assert.True(resolver.Resolve(_source, MediaPurpose.Preview).IsProxy);
    }

    [Fact]
    public void Cancel_without_a_job_returns_false() =>
        Assert.False(new MediaSourceResolver(_store, generator: null).Cancel(_source));

    private ProxyEntry CommitFake()
    {
        SourceFingerprint fingerprint = SourceFingerprint.Compute(_source);
        string temporary = _store.CreateTemporaryPath(fingerprint, ProxySettings.Default);
        File.WriteAllBytes(temporary, new byte[4321]);
        return _store.Commit(temporary, ProxyStoreTests.FakeMetadata(fingerprint, ProxySettings.Default), ProxySettings.Default);
    }
}
