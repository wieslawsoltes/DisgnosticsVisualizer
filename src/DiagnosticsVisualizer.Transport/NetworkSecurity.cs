using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DiagnosticsVisualizer.Transport;

public static class NetworkSecurity
{
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 256 || token.Any(char.IsControl))
            throw new ArgumentException("Capture tokens must contain 32–256 non-control characters.", nameof(token));
    }
    public static bool TokenEquals(string candidate, string expected)
    {
        if (candidate.Length is < 32 or > 256) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
    public static void ValidateEndpoint(Uri endpoint, params string[] schemes)
    {
        if (!endpoint.IsAbsoluteUri || !schemes.Contains(endpoint.Scheme, StringComparer.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute supported endpoint without embedded credentials, query, or fragment.");
        if (endpoint.Scheme is "tcp" or "ws" && !IsLoopbackHost(endpoint.Host))
            throw new ArgumentException("Unencrypted transport is restricted to loopback. Use TLS/WSS for remote diagnostics.");
    }
    public static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    public static byte[] DeriveDatagramKey(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
