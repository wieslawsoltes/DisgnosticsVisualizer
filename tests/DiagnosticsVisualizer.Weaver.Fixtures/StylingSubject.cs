using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

public static class StylingSubject
{
    private sealed class HostileStyle : Style
    {
        public HostileStyle() : base(s => s.OfType<Border>().Class("hot")) { }
        public override string ToString() => throw new InvalidOperationException("Diagnostics must not format styles");
    }
    public static string Exercise()
    {
        var control = new Border { Name = "StyleTarget" }; var style = new HostileStyle(); style.Setters.Add(new Setter(Visual.OpacityProperty, .4d));
        var selectorMatch = typeof(Selector).GetMethod("Match", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object Match(Selector selector, bool subscribe) => selectorMatch.Invoke(selector, [control, null, subscribe])!;
        static string Result(object match) => match.GetType().GetProperty("Result")!.GetValue(match)!.ToString()!;
        if (Result(Match(Selectors.OfType<Border>(null), false)) != "AlwaysThisType" ||
            Result(Match(Selectors.OfType<Button>(null), false)) != "NeverThisType" ||
            Result(Match(style.Selector!, false)) != "NeverThisInstance") throw new Exception("Immediate matching changed");
        var conditional = Match(style.Selector!, true);
        if (Result(conditional) != "Sometimes") throw new Exception("Conditional match changed");
        // Use the original internal attachment path; no diagnostic calls are made by the fixture.
        var attach = typeof(Style).GetMethod("TryAttach", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var frameType = attach.GetParameters()[2].ParameterType;
        attach.Invoke(style, [control, control, Enum.ToObject(frameType, 0)]);
        if (control.Opacity != 1d) throw new Exception("Inactive setter changed");
        control.Classes.Add("hot"); if (control.Opacity != .4d) throw new Exception("Active setter changed");
        control.Classes.Remove("hot"); if (control.Opacity != 1d) throw new Exception("Deactivated setter changed");
        control.Classes.Add("hot"); if (control.Opacity != .4d) throw new Exception("Reactivated setter changed");
        if (Result(Match(style.Selector!, false)) != "AlwaysThisType") throw new Exception("Immediate active match: " + Result(Match(style.Selector!, false)));
        if (Result(Match(Selectors.Name(null, "StyleTarget"), false)) != "AlwaysThisInstance") throw new Exception("Named match changed");
        try { attach.Invoke(style, [null, control, Enum.ToObject(frameType, 0)]); throw new Exception("Swallowed null target"); }
        catch (TargetInvocationException error) when (error.InnerException is ArgumentNullException) { }
        return "STYLE_WORKLOAD_OK immediate=preserved conditional=preserved activation=1/0.4/1/0.4 hostile=safe null=preserved";
    }
}
