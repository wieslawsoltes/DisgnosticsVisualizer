using System.Net;
using System.Security.Cryptography.X509Certificates;
using DiagnosticsVisualizer.Collector;
using DiagnosticsVisualizer.Transport;

try
{
    if (args.Contains("--new-token")) { Console.WriteLine(NetworkSecurity.CreateToken()); return 0; }
    if (args.Contains("--help"))
    {
        Console.WriteLine("Diagnostics collector: AVTRACE_TOKEN=<32+ chars> dotnet run --project src/DiagnosticsVisualizer.Collector -- [--http http://127.0.0.1:4317] [--tcp tcp://127.0.0.1:4318] [--udp 127.0.0.1:4319] [--origin https://your-viewer.example] [--certificate server.pfx] [--archive directory]");
        Console.WriteLine("TLS password: AVTRACE_CERT_PASSWORD. --no-udp disables UDP. --new-token generates a random capture credential."); return 0;
    }
    string? Value(string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {flag}.");
        return args[index + 1];
    }
    var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("Set AVTRACE_TOKEN; use --new-token to generate one.");
    var options = new CollectorOptions
    {
        Token = token, HttpUrl = Value("--http") ?? "http://127.0.0.1:4317",
        TcpEndpoint = new(Value("--tcp") ?? "tcp://127.0.0.1:4318"),
        UdpEndpoint = args.Contains("--no-udp") ? null : IPEndPoint.Parse(Value("--udp") ?? "127.0.0.1:4319"),
        AllowedOrigins = (Value("--origin") ?? "https://wieslawsoltes.github.io").Split(';', StringSplitOptions.RemoveEmptyEntries),
        CertificatePath = Value("--certificate"), CertificatePassword = Environment.GetEnvironmentVariable("AVTRACE_CERT_PASSWORD"),
        ArchiveDirectory = Value("--archive")
    };
    options.Validate();
    var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls(options.HttpUrl);
    if (options.CertificatePath is not null)
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword)));
    builder.Services.AddSingleton(options); builder.Services.AddSingleton<CaptureHub>(); builder.Services.AddHostedService<SocketListeners>();
    var app = builder.Build(); app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(20) });
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!context.Request.IsHttps && !NetworkSecurity.IsLoopbackHost(context.Request.Host.Host))
        { context.Response.StatusCode = 403; return; }
        await next(context);
    });
    // Operational counters only: no token, object names, values, traces or paths are exposed by health.
    app.MapGet("/health", (CaptureHub hub) => Results.Json(new
    {
        status = "ok", sourceConnected = hub.Connected, viewers = hub.Viewers,
        frames = hub.Frames, udpFrames = hub.UdpFrames, snapshotDeltaFrames = hub.SnapshotDeltaFrames,
        udpRejectedPackets = hub.UdpRejectedPackets, udpExpiredFrames = hub.UdpExpiredFrames
    }));
    app.Map("/trace", async (HttpContext context, CaptureHub hub, ILoggerFactory loggerFactory) =>
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!options.IsOriginAllowed(context.Request.Headers.Origin.ToString())) { context.Response.StatusCode = 403; return; }
        using var websocket = await context.WebSockets.AcceptWebSocketAsync();
        await using var connection = new WebSocketFrameConnection(websocket);
        try
        {
            var producer = await ConnectionAuthentication.AuthenticateServerAsync(connection, options.Token, null, context.RequestAborted);
            if (producer) await hub.RunProducerAsync(connection, context.RequestAborted);
            else await hub.RunViewerAsync(connection, context.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { loggerFactory.CreateLogger("WebSocket").LogDebug("Diagnostics WebSocket ended: {FailureType}", error.GetType().Name); }
    });
    Console.WriteLine($"Viewer gateway: {options.HttpUrl.Replace("https://", "wss://").Replace("http://", "ws://")}/trace");
    Console.WriteLine($"Target endpoint: {options.TcpEndpoint}. Capture data and tokens are not logged.");
    await app.RunAsync(); return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Collector configuration/startup failed: {error.GetType().Name}: {error.Message}"); return 1;
}
