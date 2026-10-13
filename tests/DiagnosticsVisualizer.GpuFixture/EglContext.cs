using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace DiagnosticsVisualizer.GpuFixture;

// A real Linux EGL pbuffer, used only by the executable integration workload.
// Software Mesa is useful validation, not physical-GPU performance certification.
internal sealed class EglContext : IGlContext
{
    private IntPtr _display, _surface, _context;
    private IntPtr _gl = NativeLibrary.Load("libGL.so.1");
    public GlVersion Version { get; } = new(GlProfileType.OpenGL, 3, 3);
    public GlInterface GlInterface { get; }
    public int SampleCount => 0;
    public int StencilSize => 8;
    public bool IsLost => _context == IntPtr.Zero;
    public bool CanCreateSharedContext => false;
    public bool IsSharedWith(IGlContext context) => ReferenceEquals(this, context);
    public IGlContext? CreateSharedContext(IEnumerable<GlVersion>? preferredVersions = null) => null;
    public object? TryGetFeature(Type type) => null;
    public EglContext()
    {
        try
        {
            var address = eglGetProcAddress("eglGetPlatformDisplayEXT");
            if (address == IntPtr.Zero) throw new NotSupportedException("Surfaceless EGL is required by this fixture.");
            _display = Marshal.GetDelegateForFunctionPointer<PlatformDisplay>(address)(0x31DD, IntPtr.Zero, IntPtr.Zero);
            Check(_display != IntPtr.Zero && eglInitialize(_display, out _, out _), "eglInitialize");
            Check(eglBindAPI(0x30A2), "eglBindAPI(OpenGL)");
            int[] attributes = [0x3033, 1, 0x3040, 8, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3026, 8, 0x3038];
            Check(eglChooseConfig(_display, attributes, out var config, 1, out var count) && count > 0, "eglChooseConfig");
            _surface = eglCreatePbufferSurface(_display, config, [0x3057, 64, 0x3056, 64, 0x3038]);
            Check(_surface != IntPtr.Zero, "eglCreatePbufferSurface");
            _context = eglCreateContext(_display, config, IntPtr.Zero, [0x3098, 3, 0x30FB, 3, 0x30FD, 1, 0x3038]);
            Check(_context != IntPtr.Zero, "eglCreateContext");
            using (MakeCurrent()) GlInterface = new GlInterface(Version, Resolve);
        }
        catch { Dispose(); throw; }
    }
    public IntPtr Resolve(string name)
    {
        var pointer = eglGetProcAddress(name);
        return pointer != IntPtr.Zero ? pointer : NativeLibrary.TryGetExport(_gl, name, out pointer) ? pointer : IntPtr.Zero;
    }
    public IDisposable EnsureCurrent() => MakeCurrent();
    public IDisposable MakeCurrent()
    {
        ObjectDisposedException.ThrowIf(IsLost, this);
        var oldDisplay = eglGetCurrentDisplay(); var oldContext = eglGetCurrentContext();
        var draw = eglGetCurrentSurface(0x3059); var read = eglGetCurrentSurface(0x305A);
        Check(eglMakeCurrent(_display, _surface, _surface, _context), "eglMakeCurrent");
        return new Restore(_display, oldDisplay, draw, read, oldContext);
    }
    private sealed class Restore(IntPtr display, IntPtr previousDisplay, IntPtr draw, IntPtr read, IntPtr context) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        { if (_disposed) return; _disposed = true; Check(eglMakeCurrent(previousDisplay == IntPtr.Zero ? display : previousDisplay, draw, read, context), "restore EGL context"); }
    }
    public void Dispose()
    {
        if (_display != IntPtr.Zero)
        {
            if (eglGetCurrentContext() == _context) eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_context != IntPtr.Zero) eglDestroyContext(_display, _context);
            if (_surface != IntPtr.Zero) eglDestroySurface(_display, _surface);
            eglTerminate(_display); _context = _surface = _display = IntPtr.Zero;
        }
        if (_gl != IntPtr.Zero) { NativeLibrary.Free(_gl); _gl = IntPtr.Zero; }
    }
    private static void Check(bool success, string operation)
    { if (!success) throw new InvalidOperationException($"{operation} failed: EGL 0x{eglGetError():X}."); }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PlatformDisplay(uint platform, IntPtr device, IntPtr attributes);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglBindAPI(uint api);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglChooseConfig(IntPtr display, int[] attributes, out IntPtr config, int capacity, out int count);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attributes);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shared, int[] attributes);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetCurrentContext();
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetCurrentDisplay();
    [DllImport("libEGL.so.1")] private static extern IntPtr eglGetCurrentSurface(int selector);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglDestroyContext(IntPtr display, IntPtr context);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglDestroySurface(IntPtr display, IntPtr surface);
    [DllImport("libEGL.so.1")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool eglTerminate(IntPtr display);
    [DllImport("libEGL.so.1")] private static extern int eglGetError();
}
