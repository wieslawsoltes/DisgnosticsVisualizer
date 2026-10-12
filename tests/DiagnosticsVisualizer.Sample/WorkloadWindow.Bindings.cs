using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using DiagnosticsVisualizer.Weaver.Fixtures;

namespace DiagnosticsVisualizer.Sample;

public sealed partial class WorkloadWindow
{
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        DispatcherTimer.RunOnce(ExerciseBindings, TimeSpan.FromMilliseconds(300));
    }
    private async void ExerciseBindings()
    {
        Console.WriteLine(BindingSubject.Exercise());
        var target = new Border { Name = "BindingDispatchProbe" };
        this.FindControl<WorkloadPanel>("Panel")!.Children.Add(target);
        var signal = new BindingSignal();
        using var subscription = target.Bind(Control.TagProperty, ((IObservable<object?>)signal).ToBinding());
        var expected = new object();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.PropertyChanged += (_, change) =>
        {
            if (change.Property == Control.TagProperty && ReferenceEquals(change.NewValue, expected)) delivered.TrySetResult();
        };
        await Task.Run(() => signal.Next(expected));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        if (!ReferenceEquals(target.Tag, expected)) throw new InvalidOperationException("Queued sink delivery changed value identity.");
        subscription.Dispose(); var afterDisposal = target.Tag; signal.Next(new object());
        if (signal.HasSubscriber || !ReferenceEquals(afterDisposal, target.Tag)) throw new InvalidOperationException("Observable binding disposal changed.");
        Console.WriteLine("BINDING_DISPATCH_OK identity=preserved background=published ui=delivered disposed=stable");

        var resourceTarget = new Border { Name = "BindingThemeProbe" };
        var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = resourceTarget };
        scope.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary { ["BindingThemeKey"] = "light" };
        scope.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["BindingThemeKey"] = "dark" };
        this.FindControl<WorkloadPanel>("Panel")!.Children.Add(scope);
        using var dynamic = resourceTarget.Bind(Control.TagProperty, new DynamicResourceExtension("BindingThemeKey"));
        if (!Equals(resourceTarget.Tag, "light")) throw new InvalidOperationException("Initial dynamic theme value changed.");
        scope.RequestedThemeVariant = ThemeVariant.Dark;
        if (!Equals(resourceTarget.Tag, "dark")) throw new InvalidOperationException("Dynamic theme refresh changed.");
        scope.RequestedThemeVariant = ThemeVariant.Light;
        if (!Equals(resourceTarget.Tag, "light")) throw new InvalidOperationException("Dynamic theme restoration changed.");
        Console.WriteLine("BINDING_THEME_OK light=dark=light callbacks=preserved");
    }
    private sealed class BindingSignal : IObservable<object?>
    {
        private IObserver<object?>? _observer;
        public bool HasSubscriber => Volatile.Read(ref _observer) is not null;
        public IDisposable Subscribe(IObserver<object?> observer)
        {
            if (Interlocked.CompareExchange(ref _observer, observer, null) is not null) throw new InvalidOperationException("One subscriber expected.");
            return new Lease(this, observer);
        }
        public void Next(object? value) => Volatile.Read(ref _observer)?.OnNext(value);
        private sealed class Lease(BindingSignal owner, IObserver<object?> observer) : IDisposable
        { public void Dispose() => Interlocked.CompareExchange(ref owner._observer, null, observer); }
    }
}
