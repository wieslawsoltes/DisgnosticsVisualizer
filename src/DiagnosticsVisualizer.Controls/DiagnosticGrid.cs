using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

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
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, FontSize = 11, Binding = new Binding(property) { StringFormat = property.EndsWith("Ms", StringComparison.Ordinal) || property.EndsWith("Percent", StringComparison.Ordinal) ? "{0:N3}" : null },
                Width = width < 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width)
            });
        return grid;
    }
}
