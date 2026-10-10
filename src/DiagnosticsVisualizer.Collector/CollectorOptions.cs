using System.Net;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Collector;

public sealed record CollectorOptions
{
    public string HttpUrl { get; init; } = "http://127.0.0.1:4317";
    public Uri TcpEndpoint { get; init; } = new("tcp://127.0.0.1:4318");
    public IPEndPoint? UdpEndpoint { get; init; } = new(IPAddress.Loopback, 4319);
    public required string Token { get; init; }
    public string[] AllowedOrigins { get; init; } = ["https://wieslawsoltes.github.io"];
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    public string? ArchiveDirectory { get; init; }
    public long MaxArchiveBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaxViewers { get; init; } = 16;
    public void Validate()
    {
        NetworkSecurity.ValidateToken(Token); NetworkSecurity.ValidateEndpoint(TcpEndpoint, "tcp", "tls");
        var http = new Uri(HttpUrl);
        if (http.Scheme is not ("http" or "https") || http.UserInfo.Length != 0 || http.Query.Length != 0 || http.AbsolutePath != "/")
            throw new ArgumentException("Collector HTTP URL must contain only a scheme, host, and port.");
        if (http.Scheme == "http" && !NetworkSecurity.IsLoopbackHost(http.Host))
            throw new ArgumentException("Remote HTTP listeners must use HTTPS.");
        if ((TcpEndpoint.Scheme == "tls" || http.Scheme == "https") && CertificatePath is null)
            throw new ArgumentException("A trusted server certificate is required for TLS/HTTPS.");
        if (!IPAddress.TryParse(TcpEndpoint.Host.Trim('[', ']'), out _))
            throw new ArgumentException("TCP listeners require a literal bind address, not DNS.");
        foreach (var origin in AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") ||
                parsed.UserInfo.Length != 0 || parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
                throw new ArgumentException("Origins must be exact HTTP(S) origins, never wildcards.");
        if (MaxViewers is < 1 or > 128 || MaxArchiveBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxViewers));
    }
    public bool IsOriginAllowed(string origin)
        => string.IsNullOrEmpty(origin) || AllowedOrigins.Any(allowed =>
            string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
