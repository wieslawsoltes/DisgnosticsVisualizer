using System.Text;
using System.Text.Json;

namespace DiagnosticsVisualizer.Core;

public enum SourceMappingKind { Method, XamlConstruction, XamlRoot }

/// <summary>Original, pre-weave method identity and its first visible portable-PDB sequence point.</summary>
public sealed record SourceSite(string Assembly, Guid ModuleId, int Token, string Signature,
    string Document, int Line, int Column, int EndLine, int EndColumn,
    string HashAlgorithm, string Checksum, string SourceUrl, bool AsyncKickoff)
{
    public int IlOffset { get; init; } = -1;
    public SourceMappingKind Mapping { get; init; }
    public string Location => Line > 0 ? $"{Document}:{Line}:{Column}" : "No sequence point";
    public string Identity => $"{ModuleId:N}/{Token:X8}" + (IlOffset >= 0 ? $"/IL_{IlOffset:X4}" : "");
}

/// <summary>
/// Source-bearing symbols retain the version-1 wire framing. Older readers display the
/// descriptor verbatim; updated readers decode it once during metadata ingestion.
/// No reflection, file access, or JSON processing is performed on probe entry/exit.
/// </summary>
public static class SourceSiteSymbol
{
    public const string Prefix = "\u001fAVSITE1:";
    public static string Encode(SourceSite site)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            json.WriteStringValue(site.Assembly); json.WriteStringValue(site.ModuleId);
            json.WriteNumberValue(site.Token); json.WriteStringValue(site.Signature);
            json.WriteStringValue(site.Document); json.WriteNumberValue(site.Line);
            json.WriteNumberValue(site.Column); json.WriteNumberValue(site.EndLine);
            json.WriteNumberValue(site.EndColumn); json.WriteStringValue(site.HashAlgorithm);
            json.WriteStringValue(site.Checksum); json.WriteStringValue(site.SourceUrl);
            json.WriteBooleanValue(site.AsyncKickoff);
            if (site.IlOffset >= 0 || site.Mapping != SourceMappingKind.Method)
            { json.WriteNumberValue(site.IlOffset); json.WriteNumberValue((int)site.Mapping); }
            json.WriteEndArray();
        }
        var result = Prefix + Encoding.UTF8.GetString(buffer.ToArray());
        if (Encoding.UTF8.GetByteCount(result) > TraceLimits.MaxStringBytes || !TryDecode(result, out _))
            throw new InvalidDataException("Source descriptor exceeds the wire budget or is invalid.");
        return result;
    }

    public static bool TryDecode(string symbol, out SourceSite? site)
    {
        site = null;
        if (!symbol.StartsWith(Prefix, StringComparison.Ordinal) || symbol.Length > TraceLimits.MaxStringBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(symbol.AsMemory(Prefix.Length), new JsonDocumentOptions { MaxDepth = 4 });
            var a = doc.RootElement;
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() is not (13 or 15)) return false;
            var value = new SourceSite(a[0].GetString()!, a[1].GetGuid(), a[2].GetInt32(),
                a[3].GetString()!, a[4].GetString()!, a[5].GetInt32(), a[6].GetInt32(),
                a[7].GetInt32(), a[8].GetInt32(), a[9].GetString()!, a[10].GetString()!,
                a[11].GetString()!, a[12].GetBoolean());
            if (a.GetArrayLength() == 15) value = value with { IlOffset = a[13].GetInt32(), Mapping = (SourceMappingKind)a[14].GetInt32() };
            if (value.IlOffset < -1 || (uint)value.Mapping > (uint)SourceMappingKind.XamlRoot ||
                (value.Mapping != SourceMappingKind.Method && (value.IlOffset < 0 || value.Line <= 0))) return false;
            if (string.IsNullOrEmpty(value.Assembly) || string.IsNullOrEmpty(value.Signature) || value.ModuleId == Guid.Empty ||
                (value.Token & unchecked((int)0xff000000)) != 0x06000000 ||
                value.Document is null || value.Checksum is null || value.SourceUrl is null ||
                value.Line < 0 || value.Column < 0 || value.EndLine < value.Line || value.EndColumn < 0 ||
                (value.EndLine == value.Line && value.EndColumn < value.Column) ||
                (value.Line > 0 && value.Document.Length == 0)) return false;
            var hashLength = value.HashAlgorithm switch { "" => 0, "SHA1" => 40, "SHA256" => 64, _ => -1 };
            if (hashLength < 0 || value.Checksum.Length != hashLength || value.Checksum.Any(c => !Uri.IsHexDigit(c))) return false;
            if (value.SourceUrl.Length > 0 && !IsSafeSourceUrl(value.SourceUrl)) return false;
            site = value; return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { return false; }
    }

    public static bool IsSafeSourceUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}
