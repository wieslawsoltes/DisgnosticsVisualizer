using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

public static class BindingSubject
{
    public sealed class Model : INotifyPropertyChanged
    {
        private object? _value = "initial";
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? Value { get => _value; set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); } }
    }
    private sealed class Converter : IValueConverter
    {
        public int Forward, Backward;
        public object? Convert(object? value, Type type, object? parameter, CultureInfo culture)
        {
            Forward++;
            if (value is string s && s == "throw") throw new InvalidOperationException("Original converter failure");
            return value is string text && text == "nothing" ? BindingOperations.DoNothing : value;
        }
        public object? ConvertBack(object? value, Type type, object? parameter, CultureInfo culture) { Backward++; return value; }
    }
    public static string Exercise()
    {
        var model = new Model(); var converter = new Converter(); var target = new Border();
        var binding = new Binding(nameof(Model.Value)) { Source = model, Mode = BindingMode.TwoWay, Converter = converter, FallbackValue = "fallback" };
        var subscription = target.Bind(Control.TagProperty, binding);
        if (!Equals(target.Tag, "initial")) throw new InvalidOperationException("Initial binding value changed");
        model.Value = "source"; if (!Equals(target.Tag, "source")) throw new InvalidOperationException("Source update changed");
        target.SetCurrentValue(Control.TagProperty, "target"); if (!Equals(model.Value, "target")) throw new InvalidOperationException("TwoWay update changed");
        model.Value = null; if (target.Tag is not null) throw new InvalidOperationException("Null result changed");
        model.Value = "nothing"; if (target.Tag is not null) throw new InvalidOperationException("DoNothing result changed");
        model.Value = "throw"; if (!Equals(target.Tag, "fallback")) throw new InvalidOperationException("Converter fallback changed");
        subscription.Dispose(); var detached = target.Tag; model.Value = "after disposal";
        if (!Equals(detached, target.Tag)) throw new InvalidOperationException("Disposed binding still writes");
        var missingTarget = new Border(); using var missing = missingTarget.Bind(Control.TagProperty, new Binding("NotAProperty") { Source = model, FallbackValue = "missing" });
        if (!Equals(missingTarget.Tag, "missing")) throw new InvalidOperationException("Missing-node fallback changed");
        var resourceTarget = new Border(); resourceTarget.Resources["BindingProbeKey"] = "resource-one";
        var resource = resourceTarget.Bind(Control.TagProperty, new DynamicResourceExtension("BindingProbeKey"));
        if (!Equals(resourceTarget.Tag, "resource-one")) throw new InvalidOperationException("Initial resource binding changed");
        resourceTarget.Resources["BindingProbeKey"] = "resource-two";
        if (!Equals(resourceTarget.Tag, "resource-two")) throw new InvalidOperationException("Resource invalidation changed");
        resource.Dispose(); var resourceDetached = resourceTarget.Tag; resourceTarget.Resources["BindingProbeKey"] = "resource-three";
        if (!Equals(resourceDetached, resourceTarget.Tag)) throw new InvalidOperationException("Resource unsubscribe changed");
        return $"BINDING_WORKLOAD_OK forward={converter.Forward} backward={converter.Backward} null=preserved sentinel=preserved fallback=preserved resources=updated disposed=stable";
    }
}
