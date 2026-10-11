namespace DiagnosticsVisualizer.Weaver.Fixtures;

// Synthetic compiler sequence points. The separate packaged integration executes real XamlIl output.
public sealed class XamlNode
{
    public int Value;
    public XamlNode(bool fail) { if (fail) throw new InvalidOperationException("Original constructor failure"); }
}
public static class XamlSubject
{
    public static XamlNode Build(bool fail)
    {
#line 12 "Widget.axaml"
        var node = new XamlNode(fail);
#line default
        return node;
    }
    public static void __AvaloniaXamlIlPopulate(object services, XamlNode root)
    {
#line 30 "Widget.axaml"
        root.Value = 17;
#line default
    }
}
