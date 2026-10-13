using System.Runtime.InteropServices;
using DiagnosticsVisualizer.Agent.Avalonia;

namespace DiagnosticsVisualizer.GpuFixture;

// Separate explicit backend test. The default rendering workload makes no diagnostics API calls.
internal static class OpenGlBackendChecks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BufferNames(int count, ref uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Enable(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetString(uint name);
    public static void Run(EglContext context)
    {
        using var current = context.MakeCurrent();
        var backend = new OpenGlTimestampBackend(context.Resolve, () => context.IsLost);
        if (backend.Site.CounterBits != 64) throw new NotSupportedException("Mesa fixture requires 64-bit timestamp queries.");
        var query = backend.CreateQuery();
        try
        {
            if (context.GlInterface.GetError() != 0) throw new InvalidOperationException("Unexpected preexisting fixture error.");
            var enable = Load<Enable>("glEnable"); enable(uint.MaxValue); // Deliberately establish a known application GL error.
            backend.Timestamp(query); backend.TryRead(query, out _);
            if (context.GlInterface.GetError() != 0x0500 || context.GlInterface.GetError() != 0)
                throw new InvalidOperationException("Backend consumed or changed the application's error state.");
            var version = Marshal.PtrToStringUTF8(Load<GetString>("glGetString")(0x1F02)) ?? "";
            // CI requests software Mesa core GL (4.5+), which supports query buffers.
            var major = int.Parse(version.Split('.')[0]); var minor = int.Parse(version.Split('.')[1].Split(' ')[0]);
            if (major < 4 || major == 4 && minor < 4) throw new NotSupportedException("Query-buffer safety gate requires GL 4.4+ in this validation context.");
            var gen = Load<BufferNames>("glGenBuffers"); var delete = Load<BufferNames>("glDeleteBuffers"); var bind = Load<BindBuffer>("glBindBuffer");
            uint buffer = 0; gen(1, ref buffer);
            try
            {
                bind(0x9192, buffer);
                if (backend.CanRead || backend.TryRead(query, out _)) throw new InvalidOperationException("Backend read a CPU pointer while GL_QUERY_BUFFER was bound.");
                context.GlInterface.GetIntegerv(0x9193, out var bound);
                if ((uint)bound != buffer) throw new InvalidOperationException("Backend changed the application's query buffer binding.");
            }
            finally { bind(0x9192, 0); delete(1, ref buffer); }
            if (context.GlInterface.GetError() != 0) throw new InvalidOperationException("Query-buffer test caused a GL error.");
            Console.WriteLine("GPU_BACKEND_OK preserved-error=1 preserved-query-buffer=1 wait-inserted=0");
        }
        finally { backend.DeleteQuery(query); }
        T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(context.Resolve(name));
    }
}
