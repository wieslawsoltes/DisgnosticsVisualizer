using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace NativeFixture;
public static class Program
{
    private static int _finally, _iterations;
    private static readonly int[] Values = [17];
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "control") return Controlled();
        var watch = Stopwatch.StartNew();
        // Warm before attachment; loop continues without any application diagnostics reference.
        for (var n = 0; n < 10000; n++) Work();
        using var stop=new CancellationTokenSource();
        var workers=Enumerable.Range(0,3).Select(_=>new Thread(()=>{while(!stop.IsCancellationRequested){Work();Thread.Sleep(2);}})).ToArray();
        foreach(var worker in workers)worker.Start();
        Console.WriteLine($"READY {Environment.ProcessId}"); Console.Out.Flush();
        while (watch.Elapsed < TimeSpan.FromSeconds(args.Length == 0 ? 15 : int.Parse(args[0]))) { Work(); Thread.Sleep(1); }
        stop.Cancel();foreach(var worker in workers)worker.Join();
        Console.WriteLine(JsonSerializer.Serialize(new { status = "verified", iterations = _iterations, finallyCount = _finally }));
        return _finally == _iterations ? 0 : 3;
    }
    private static readonly ManualResetEventSlim HeldStarted = new(), HeldRelease = new();
    private static int Controlled()
    {
        for (var i = 0; i < 100; i++) Work();
        Console.WriteLine($"READY {Environment.ProcessId}");
        Thread? held = null;
        while (Console.ReadLine() is { } command)
        {
            if (command == "run") { for (var i = 0; i < 50; i++) Work(); Console.WriteLine("RUN_OK"); }
            else if (command == "hold") { HeldStarted.Reset(); HeldRelease.Reset(); held = new Thread(Held); held.Start(); if (!HeldStarted.Wait(5000)) return 4; Console.WriteLine("HOLD_OK"); }
            else if (command == "release") { HeldRelease.Set(); if (held is null || !held.Join(5000)) return 5; held = null; Console.WriteLine("RELEASE_OK"); }
            else if (command == "quit") { HeldRelease.Set(); held?.Join(); Console.WriteLine("CONTROL_WORKLOAD_OK"); return _finally == _iterations ? 0 : 6; }
            else return 7;
            Console.Out.Flush();
        }
        return 8;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Held()
    { HeldStarted.Set(); if (!HeldRelease.Wait(10000) || Add(2, 3) != 5) throw new InvalidOperationException("Held invocation changed"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Work()
    {
        Interlocked.Increment(ref _iterations);
        if (FinallyCalls()!=13 || FinallyThrows()!=17 || Rethrow()!=19 || NestedFilter()!=23) throw new InvalidOperationException("Exception semantics changed");
        if (Add(3, 5) != 8 || Generic(9) != 9 || ReadRef() != 17 || Caller(3) != 7 || Filtered() != 11) throw new InvalidOperationException("Behavior changed");
        try { Throwing(); return -1; } catch (InvalidOperationException e) when (e.Message == "Expected fixture exception") { return 1; }
        finally { Interlocked.Increment(ref _finally); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int FinallyCalls(){try { try{Throwing();} finally{if(Add(1,2)!=3)throw new Exception("Finally behavior");} }catch(InvalidOperationException){return 13;}return -1;}
    [MethodImpl(MethodImplOptions.NoInlining)] public static int FinallyThrows(){try{try{Throwing();}finally{Throwing();}}catch(InvalidOperationException){return 17;}return -1;}
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Rethrow(){try{try{Throwing();}catch(InvalidOperationException){Add(1,2);throw;}}catch(InvalidOperationException){return 19;}return -1;}
    [MethodImpl(MethodImplOptions.NoInlining)] public static int NestedFilter(){try{Throwing();}catch(InvalidOperationException)when(Filtered()==11){return 23;}return -1;}
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Add(int a,int b) => a + b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static T Generic<T>(T value) => value;
    [MethodImpl(MethodImplOptions.NoInlining)] public static ref int ReadRef() => ref Values[0];
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Throwing() { throw new InvalidOperationException("Expected fixture exception"); }
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Filtered() { try { Throwing(); return -1; } catch (InvalidOperationException e) when (e.Message.Length > 0) { return 11; } }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static int Inlined(int value) => value * 2;
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Caller(int value) => Inlined(value) + 1;
}
