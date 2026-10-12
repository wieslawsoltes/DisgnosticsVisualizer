using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Styling;

namespace DiagnosticsVisualizer.Sample;

public sealed class PropertySubject : Border
{
    public static readonly StyledProperty<int> NumberProperty = AvaloniaProperty.Register<PropertySubject, int>(nameof(Number), inherits: true, coerce: (_, v) => Math.Clamp(v, 0, 10));
    public static readonly DirectProperty<PropertySubject, int> DirectProperty = AvaloniaProperty.RegisterDirect<PropertySubject, int>(nameof(Direct), o => o.Direct, (o, v) => o.Direct = v);
    public static readonly AttachedProperty<int> MarkerProperty = AvaloniaProperty.RegisterAttached<PropertySubject, Border, int>("Marker");
    private int _direct;
    public int Number { get => GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public int Direct { get => _direct; set => SetAndRaise(DirectProperty, ref _direct, value); }
    public static string Exercise()
    {
        var target = new PropertySubject { Name = "PropertyHistoryProbe" };
        target.Number = 40; var coerced = target.Number;
        target.Direct = 3; target.Direct = 3;
        target.SetValue(MarkerProperty, 7);
        using (target.SetValue(NumberProperty, 2, BindingPriority.Animation))
        {
            target.Number = 8;
            if (target.Number != 2 || target.GetBaseValue(NumberProperty).Value != 8) throw new InvalidOperationException("Animation/base contract changed.");
        }
        if (target.Number != 8) throw new InvalidOperationException("Animation disposal failed.");
        target.PropertyChanged += (_, e) => { if (e.Property == NumberProperty && target.Number == 5) target.Number = 6; };
        target.Number = 5; var reentrant = target.Number;
        target.SetCurrentValue(NumberProperty, 4); target.CoerceValue(NumberProperty); target.ClearValue(NumberProperty);
        var parent = new PropertySubject { Number = 4 }; parent.Child = target; var inherited = target.Number;
        parent.Number = 7; var inheritedNext = target.Number;
        EventHandler<AvaloniaPropertyChangedEventArgs> fault = (_, e) => { if (e.Property == NumberProperty) throw new InvalidOperationException("Original subscriber failure"); };
        target.PropertyChanged += fault;
        var error = "";
        try { target.Number = 9; } catch (InvalidOperationException e) { error = e.Message; }
        target.PropertyChanged -= fault;
        var nullError = ""; try { target.SetValue((StyledProperty<int>)null!, 5); } catch (ArgumentNullException e) { nullError = e.ParamName ?? ""; }
        return $"{coerced}|{target.Direct}|{target.GetValue(MarkerProperty)}|{reentrant}|{inherited}|{inheritedNext}|{target.Number}|{error}|{nullError}";
    }
}
