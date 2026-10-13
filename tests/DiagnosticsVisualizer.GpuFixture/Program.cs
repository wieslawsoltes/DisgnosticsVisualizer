using System.Reflection;
using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Surfaces;
using DiagnosticsVisualizer.GpuFixture;
using SkiaSharp;

// No diagnostics API calls. The Build package injects bootstrap and backend hooks
// into output copies; this workload drives the actual pinned Skia GL session.
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The EGL validation fixture currently runs on Linux.");
using var context = new EglContext();
if (args.SequenceEqual(new[] { "--backend-check" })) { OpenGlBackendChecks.Run(context); return; }
var assembly = Assembly.Load("Avalonia.Skia");
var gpuType = assembly.GetType("Avalonia.Skia.GlSkiaGpu", true)!;
using var gpu = (IDisposable)Activator.CreateInstance(gpuType, [context, null])!;
var platform = new PbufferSurface();
using var target = (IDisposable)gpuType.GetMethod("TryCreateRenderTarget")!.Invoke(gpu, [new object[] { platform }])!;
var begin = target.GetType().GetMethod("BeginRenderingSession", Type.EmptyTypes)!;
for (var frame = 0; frame < 48; frame++)
{
    using (var session = (IDisposable)begin.Invoke(target, null)!)
    {
        var surface = (SKSurface)session.GetType().GetProperty("SkSurface")!.GetValue(session)!;
        surface.Canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = frame % 2 == 0 ? SKColors.Teal : SKColors.Coral, IsAntialias = true };
        surface.Canvas.DrawRect(new SKRect(4, 4, 48, 48), paint);
    }
    // Real application submission; this is not an injected wait for GPU completion.
    Thread.Sleep(3);
}
if (platform.Completed != 48) throw new InvalidOperationException("Instrumentation changed platform disposal behavior.");
using (context.MakeCurrent())
{
    var error = context.GlInterface.GetError();
    if (error != 0) throw new InvalidOperationException($"GL error after workload: 0x{error:X}.");
}
Console.WriteLine("GPU_WORKLOAD_OK frames=48 disposed=48");

internal sealed class PbufferSurface : IGlPlatformSurface
{
    public int Completed;
    public IGlPlatformSurfaceRenderTarget CreateGlRenderTarget(IGlContext context) => new Target(context, this);
    private sealed class Target(IGlContext context, PbufferSurface owner) : IGlPlatformSurfaceRenderTarget
    {
        public IGlPlatformSurfaceRenderingSession BeginDraw() => new Session(context, owner);
        public void Dispose() { }
    }
    private sealed class Session : IGlPlatformSurfaceRenderingSession
    {
        private readonly IDisposable _current;
        private readonly PbufferSurface _owner;
        private bool _disposed;
        public IGlContext Context { get; }
        public PixelSize Size => new(64, 64);
        public double Scaling => 1;
        public bool IsYFlipped => false;
        public Session(IGlContext context, PbufferSurface owner) { Context = context; _owner = owner; _current = context.MakeCurrent(); }
        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("Platform session disposed twice.");
            _disposed = true;
            Context.GlInterface.Flush(); // Original application's submission, also orders the injected end timestamp.
            _owner.Completed++; _current.Dispose();
        }
    }
}
