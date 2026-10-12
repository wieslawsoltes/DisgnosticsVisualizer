using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using System.Globalization;

namespace DiagnosticsVisualizer.Controls;

public static class DiagnosticGrid
{
    public static DataGrid Create(params (string Header, string Property, double Width)[] columns)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserReorderColumns = true, CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single, RowHeight = 26, ColumnHeaderHeight = 28,
            BorderThickness = new Thickness(0), FontSize = 11
        };
        foreach (var (header, property, width) in columns)
        {
            // Reserve header text plus padding, sort indicator and resize grip. Never compress units away.
            var measured = new FormattedText(header, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default), 11, Brushes.White).WidthIncludingTrailingWhitespace;
            var minimum = Math.Max(width < 0 ? 180 : width, measured + 52);
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, FontSize = 11, Binding = new Binding(property) { StringFormat = property.EndsWith("Ms", StringComparison.Ordinal) || property.EndsWith("Percent", StringComparison.Ordinal) ? "{0:N3}" : null },
                MinWidth = minimum,
                Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(minimum)
            });
        }
        return grid;
    }
}
