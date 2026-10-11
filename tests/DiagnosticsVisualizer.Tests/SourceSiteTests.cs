using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class SourceSiteTests
{
    internal static SourceSite Site(string source = "line one\nline two\nline three\n") => new("Application", Guid.Parse("918ce3ed-a58b-4ea3-85ca-c624feadfd38"), 0x06000001,
        "System.Void Application.View::Measure()", "View.cs", 2, 1, 2, 9, "SHA256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))), "https://example.org/src/View.cs", false);

    [Fact] public async Task SourceMetadataSurvivesWireIngestionAndBinaryReExport()
    {
        var site = Site(); var descriptor = SourceSiteSymbol.Encode(site);
        var block = new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Symbols = [new(1, descriptor)],
            Events = [new(1, 0, 10, 5, 0, 0, 1, 1, Subsystem.Layout)] };
        var store = new TraceStore(); store.Add(TraceCodec.Decode(TraceCodec.Encode(block)));
        var view = store.GetView(); Assert.Equal(site.Signature, view.Name(view.Events[0])); Assert.Equal(site, view.SourceSites[1]);
        using var binary = new MemoryStream(); await TraceExport.WriteBinaryAsync(binary, view); binary.Position = 0;
        var copy = new TraceStore(); await TraceFile.ReadAsync(binary, b => { copy.Add(b); return ValueTask.CompletedTask; }, false);
        Assert.Equal(site, copy.GetView().SourceSites[1]);
        using var chrome = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(chrome, view);
        Assert.Contains(site.Identity, Encoding.UTF8.GetString(chrome.ToArray()));
    }

    [Theory] [InlineData("plain legacy name")] [InlineData("\u001fAVSITE1:[]")] [InlineData("\u001fAVSITE1:{}")]
    [InlineData("\u001fAVSITE1:[null]")] [InlineData("\u001fAVSITE1:not json")]
    public void InvalidDescriptorsDoNotBreakLegacyReaders(string value) => Assert.False(SourceSiteSymbol.TryDecode(value, out _));

    [Fact] public void UnsafeSourceUrlsAndInvalidSequencePointsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => SourceSiteSymbol.Encode(Site() with { SourceUrl = "file:///etc/passwd" }));
        Assert.Throws<InvalidDataException>(() => SourceSiteSymbol.Encode(Site() with { SourceUrl = "https://user:password@example.org/source" }));
        Assert.Throws<InvalidDataException>(() => SourceSiteSymbol.Encode(Site() with { EndLine = 1 }));
        Assert.Throws<InvalidDataException>(() => SourceSiteSymbol.Encode(Site() with { Checksum = "not-a-checksum" }));
    }

    [Fact] public void MatchingFileIsVerifiedAndExactLineIsSelected()
    {
        const string source = "line one\r\nline two\r\nline three\r\n";
        var excerpt = SourceExcerpt.Create(Encoding.UTF8.GetBytes(source), Site(source));
        Assert.True(excerpt.ChecksumVerified); Assert.Equal(2, excerpt.FocusLine);
        Assert.Equal("     2  line two", excerpt.Text[excerpt.SelectionStart..excerpt.SelectionEnd]);
        Assert.Throws<InvalidDataException>(() => SourceExcerpt.Create(Encoding.UTF8.GetBytes("wrong revision"), Site(source)));
    }

    [Fact] public async Task SourceReadIsBoundedAndRejectsInvalidUtf8()
    {
        using var large = new MemoryStream(new byte[SourceExcerpt.MaxFileBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceExcerpt.ReadAsync(large, Site()));
        var noHash = Site() with { HashAlgorithm = "", Checksum = "" };
        Assert.Throws<DecoderFallbackException>(() => SourceExcerpt.Create(new byte[] { 0xff, 0xfe }, noHash));
        Assert.False(SourceExcerpt.Create(Encoding.UTF8.GetBytes("first\nsecond"), noHash).ChecksumVerified);
    }
    [Fact] public async Task ChromeExportEmitsHandoffsButNotOrdinaryNesting()
    {
        var store = new TraceStore();
        store.Add(new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Symbols = [new(1, "scope")],
            Events = [new(1, 0, 10, 10, 0, 0, 1, 1, Subsystem.Application),
                new(2, 1, 12, 2, 0, 0, 1, 1, Subsystem.Application),
                new(3, 1, 15, 2, 0, 0, 2, 1, Subsystem.Application),
                new(4, 1, 25, 2, 0, 0, 1, 1, Subsystem.Application),
                new(5, 99, 30, 2, 0, 0, 2, 1, Subsystem.Application)] });
        using var output = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(output, store.GetView());
        using var json = JsonDocument.Parse(output.ToArray());
        var flows = json.RootElement.GetProperty("traceEvents").EnumerateArray()
            .Where(e => e.GetProperty("ph").GetString() is "s" or "f").ToArray();
        Assert.Equal(4, flows.Length);
        Assert.Equal(new[] { "causal-3", "causal-3", "causal-4", "causal-4" }, flows.Select(e => e.GetProperty("id").GetString()));
        for (var i = 0; i < flows.Length; i += 2)
            Assert.True(flows[i].GetProperty("ts").GetDouble() <= flows[i + 1].GetProperty("ts").GetDouble());
    }

    [Fact] public void ComparisonAggregatesIdenticalSignaturesAcrossModuleIdentities()
    {
        var before = new AnalysisResult([new("Measure", Subsystem.Layout, 2, 5, 3, 3),
            new("Measure", Subsystem.Layout, 1, 4, 4, 4)], [], []);
        var after = new AnalysisResult([new("Measure", Subsystem.Layout, 4, 12, 9, 3)], [], []);
        var row = Assert.Single(TraceComparison.Compare(before, after));
        Assert.Equal(3, row.BaselineCount); Assert.Equal(4, row.CurrentCount);
        Assert.Equal(7, row.BaselineSelfMs); Assert.Equal(2, row.DeltaMs);
    }

}
