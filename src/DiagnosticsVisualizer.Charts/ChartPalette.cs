using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Charts;

public static class ChartPalette
{
    public static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1F2124"));
    public static readonly IBrush Alternate = new SolidColorBrush(Color.Parse("#2B2D30"));
    public static readonly IBrush Foreground = new SolidColorBrush(Color.Parse("#DFE1E5"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9DA0A8"));
    public static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4C9FFF"));
    public static readonly Pen Grid = new(new SolidColorBrush(Color.Parse("#393C40")), 1);
    private static readonly IBrush[] Brushes = new[]
    {
        "#6AA9FF", "#B394FF", "#CB8DFF", "#F0BD65", "#ED9A75", "#70D8BA", "#7F9CEC",
        "#EE8FA6", "#A5C6EB", "#58C7D8", "#A9D989", "#D2DB83", "#7398D6", "#93B8DC",
        "#EC8DA4", "#D59CDD", "#F0CF79", "#D3A989", "#B7A8F0", "#7891B3", "#ADB7C7"
    }.Select(text => (IBrush)new SolidColorBrush(Color.Parse(text))).ToArray();
    public static IBrush For(Subsystem category) => Brushes[Math.Clamp((int)category, 0, Brushes.Length - 1)];
    public static FormattedText Text(string text, double size = 11, IBrush? foreground = null)
        => new(text, CultureInfo.CurrentCulture, default, new Typeface(FontFamily.Default), size, foreground ?? Foreground);
    public static void Label(DrawingContext context, string text, Point point, double size = 11, IBrush? foreground = null)
        => context.DrawText(Text(text, size, foreground), point);
    public static string ShortName(string name)
    {
        var paren = name.IndexOf('('); if (paren >= 0) name = name[..paren];
        var space = name.LastIndexOf(' '); if (space >= 0) name = name[(space + 1)..];
        var separator = name.LastIndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? name[(separator + 2)..] : name;
    }
}
