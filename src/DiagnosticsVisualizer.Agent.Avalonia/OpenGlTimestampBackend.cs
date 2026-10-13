using System.Globalization;
using System.Runtime.InteropServices;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>
/// Desktop GL timestamp queries. The caller must own a current context on this thread.
/// Never changes query bindings, consumes GL errors, flushes, or waits for results.
/// </summary>
public sealed class OpenGlTimestampBackend : IGpuTimestampBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetStringDelegate(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr GetStringIndexDelegate(uint name, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetIntegerDelegate(uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetQueryDelegate(uint target, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void QueryNamesDelegate(int count, ref uint value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void TimestampDelegate(uint query, uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void AvailableDelegate(uint query, uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ResultDelegate(uint query, uint name, out ulong value);
    private readonly Func<bool> _isLost;
    private QueryNamesDelegate? _generate, _delete;
    private TimestampDelegate? _timestamp;
    private AvailableDelegate? _available;
    private ResultDelegate? _result;
    private GetIntegerDelegate? _integer;
    private bool _queryBuffers;
    public GpuTimingSite Site { get; private set; }
    public bool IsLost => _isLost();
    public bool CanRead
    {
        get
        {
            if (Site.CounterBits != 64 || IsLost) return false;
            if (!_queryBuffers) return true;
            _integer!(0x9193, out var buffer); // GL_QUERY_BUFFER_BINDING: pointer parameters otherwise become offsets.
            return buffer == 0;
        }
    }
    public OpenGlTimestampBackend(Func<string, IntPtr> resolve, Func<bool> isLost, string name = "Skia OpenGL render session (pre-present)")
    {
        ArgumentNullException.ThrowIfNull(resolve); ArgumentNullException.ThrowIfNull(isLost);
        _isLost = isLost;
        Site = new(name, "OpenGL", "", "", 0, "Requires desktop OpenGL 3.3+ and 64-bit timestamps.");
        try
        {
            if (IsLost) { Site = Site with { Limitation = "Context lost before query initialization." }; return; }
            var text = Load<GetStringDelegate>("glGetString");
            string String(uint key) => Clean(Marshal.PtrToStringUTF8(text(key)) ?? "");
            var version = String(0x1F02);
            Site = Site with { Vendor = String(0x1F00), Renderer = String(0x1F01) };
            if (version.StartsWith("OpenGL ES", StringComparison.Ordinal)) return;
            var parts = version.Split(' ')[0].Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) || major < 3 || major == 3 && minor < 3) return;
            var query = Load<GetQueryDelegate>("glGetQueryiv"); query(0x8E28, 0x8864, out var bits);
            if (bits != 64) { Site = Site with { Limitation = "Counter width is not 64 bits; ambiguous wraparound is not inferred." }; return; }
            _integer = Load<GetIntegerDelegate>("glGetIntegerv");
            _queryBuffers = major > 4 || major == 4 && minor >= 4;
            if (!_queryBuffers)
            {
                var indexed = Load<GetStringIndexDelegate>("glGetStringi");
                _integer(0x821D, out var count);
                if (count is < 0 or > 16384) return;
                for (uint i = 0; i < count; i++)
                    if (Marshal.PtrToStringUTF8(indexed(0x1F03, i)) == "GL_ARB_query_buffer_object") { _queryBuffers = true; break; }
            }
            _generate = Load<QueryNamesDelegate>("glGenQueries"); _delete = Load<QueryNamesDelegate>("glDeleteQueries");
            _timestamp = Load<TimestampDelegate>("glQueryCounter"); _available = Load<AvailableDelegate>("glGetQueryObjectiv");
            _result = Load<ResultDelegate>("glGetQueryObjectui64v");
            Site = Site with { CounterBits = 64, Limitation = "Uncalibrated device clock. Includes intervening work; excludes presentation. May be a software renderer." };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or EntryPointNotFoundException)
        { Site = Site with { CounterBits = 0, Limitation = "Required GL timestamp entry points are unavailable." }; }
        T Load<T>(string symbol) where T : Delegate
        {
            var pointer = resolve(symbol);
            if (pointer == IntPtr.Zero || pointer == new IntPtr(-1) || pointer == new IntPtr(1) || pointer == new IntPtr(2) || pointer == new IntPtr(3))
                throw new EntryPointNotFoundException(symbol);
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
    }
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(512).ToArray());
    public uint CreateQuery()
    { if (Site.CounterBits != 64 || IsLost) return 0; uint id = 0; _generate!(1, ref id); return id; }
    public void DeleteQuery(uint query) { if (query != 0 && !IsLost) _delete!(1, ref query); }
    public void Timestamp(uint query) => _timestamp!(query, 0x8E28);
    public bool TryRead(uint query, out ulong nanoseconds)
    {
        nanoseconds = 0;
        if (!CanRead) return false;
        _available!(query, 0x8867, out var ready); // GL_QUERY_RESULT_AVAILABLE
        if (ready == 0) return false;
        _result!(query, 0x8866, out nanoseconds); // GL_QUERY_RESULT, only after availability.
        return true;
    }
}
