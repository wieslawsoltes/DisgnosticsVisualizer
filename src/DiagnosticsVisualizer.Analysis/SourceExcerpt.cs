using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record SourceExcerpt(string Text, bool ChecksumVerified, int FirstLine, int FocusLine,
    int SelectionStart, int SelectionEnd)
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    /// <summary>Read only an explicitly supplied stream. Never resolve paths or fetch URLs from a trace.</summary>
    public static async Task<SourceExcerpt> ReadAsync(Stream stream, SourceSite site, CancellationToken cancellationToken = default)
    {
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, 16 * 1024), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > MaxFileBytes) throw new InvalidDataException("Source files are limited to 2 MiB.");
                bytes.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        return Create(bytes.ToArray(), site);
    }

    public static SourceExcerpt Create(ReadOnlySpan<byte> bytes, SourceSite site)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("Source files are limited to 2 MiB.");
        var verified = false;
        if (site.Checksum.Length > 0)
        {
            // SHA1 is used only to compare a legacy PDB document fingerprint, never for authentication.
            var actual = site.HashAlgorithm switch
            { "SHA256" => SHA256.HashData(bytes), "SHA1" => SHA1.HashData(bytes), _ => throw new InvalidDataException("Unknown source checksum algorithm.") };
            if (!Convert.ToHexString(actual).Equals(site.Checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected file does not match the captured PDB checksum. Build and capture the matching revision.");
            verified = true;
        }
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var lines = text.Split('\n');
        if (site.Line <= 0 || site.Line > lines.Length) throw new InvalidDataException("The captured sequence point is outside this source document.");
        var first = Math.Max(1, site.Line - 40); var last = Math.Min(lines.Length, site.Line + 40);
        var output = new StringBuilder(); var start = 0; var end = 0;
        for (var line = first; line <= last; line++)
        {
            if (line == site.Line) start = output.Length;
            output.Append(line.ToString().PadLeft(6)).Append("  ").Append(lines[line - 1].TrimEnd('\r')).Append('\n');
            if (line == site.Line) end = output.Length - 1;
        }
        return new(output.ToString(), verified, first, site.Line, start, end);
    }
}
