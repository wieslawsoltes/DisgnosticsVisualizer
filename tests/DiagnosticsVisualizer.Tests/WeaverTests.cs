using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Weaver;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class WeaverTests
{
    private sealed class FixtureContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => name.Name == typeof(Probe).Assembly.GetName().Name ? typeof(Probe).Assembly :
               name.Name == typeof(Subsystem).Assembly.GetName().Name ? typeof(Subsystem).Assembly : null;
    }
    [Fact] public void RealRewritePreservesReturnsFiltersFinallyGenericsAndExceptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avtrace-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var source = typeof(DiagnosticsVisualizer.Weaver.Fixtures.Subject).Assembly.Location;
        var target = Path.Combine(directory, Path.GetFileName(source)); File.Copy(source, target);
        var sourcePdb = Path.ChangeExtension(source, ".pdb");
        if (File.Exists(sourcePdb)) File.Copy(sourcePdb, Path.ChangeExtension(target, ".pdb"));
        try
        {
            var original = SHA256.HashData(File.ReadAllBytes(target));
            var options = new WeaveOptions(directory) { Include = [Path.GetFileName(target)], Bootstrap = false };
            var report = WeaveEngine.Weave(options);
            Assert.Contains(report.Assemblies[0].Methods, m => m.Signature.Contains("ReferenceReturn") && m.Status == "Skipped");
            var woven = SHA256.HashData(File.ReadAllBytes(target)); Assert.NotEqual(Convert.ToHexString(original), Convert.ToHexString(woven));
            WeaveEngine.Weave(options); Assert.Equal(woven, SHA256.HashData(File.ReadAllBytes(target)));
            using (var session = Probe.Start())
            {
                var context = new FixtureContext();
                try
                {
                    var assembly = context.LoadFromAssemblyPath(target); var type = assembly.GetType("DiagnosticsVisualizer.Weaver.Fixtures.Subject")!;
                    var instance = Activator.CreateInstance(type); var method = type.GetMethod("Compute")!;
                    Assert.Equal(7, method.Invoke(instance, [0])); Assert.Equal(11, method.Invoke(instance, [1]));
                    Assert.Equal(33, method.Invoke(instance, [3])); Assert.Equal(8, method.Invoke(instance, [4]));
                    var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(instance, [2]));
                    Assert.IsType<InvalidOperationException>(error.InnerException); Assert.Equal("original exception", error.InnerException!.Message);
                    Assert.Equal(15, type.GetField("FinallyCount")!.GetValue(null));
                    Assert.Equal(123, type.GetMethod("Echo")!.MakeGenericMethod(typeof(int)).Invoke(null, [123]));
                    var events = session.Drain().Events; Assert.Equal(6, events.Length); Assert.All(events, e => Assert.True(e.Duration >= 0));
                    Assert.Equal(0, session.ProbeFaults);
                }
                finally { context.Unload(); }
            }
            WeaveEngine.Restore(directory); Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(target)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
