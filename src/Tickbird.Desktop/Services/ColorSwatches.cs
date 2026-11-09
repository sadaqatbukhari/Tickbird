using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tickbird.Core.Settings;

namespace Tickbird.Desktop.Services;

/// <summary>A row of colour swatches to pick from, with a first choice that means "no colour".</summary>
public sealed class ColorSwatches : StackPanel
{
    private static readonly IBrush Ring = Brush.Parse("#17233F");
    private readonly List<(string? Hex, Border Swatch)> swatches = new();
    private string? value;

    /// <summary>The chosen colour as #RRGGBB, or null for none.</summary>
    public string? Value
    {
        get => value;
        set { this.value = ConnectionColors.Normalize(value); Refresh(); }
    }

    public event Action? Changed;

    public ColorSwatches(string? initial, string noneTip = "No colour")
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        Add(null, noneTip);
        foreach (var (name, hex) in ConnectionColors.Palette) Add(hex, name);
        value = ConnectionColors.Normalize(initial);
        Refresh();
    }

    private void Add(string? hex, string tip)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2),
            Background = hex == null ? Brushes.White : Brush.Parse(hex), Tag = hex, Cursor = new Cursor(StandardCursorType.Hand),
            Child = hex == null ? new TextBlock { Text = "✕", FontSize = 10, Foreground = Brush.Parse("#7284A5"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null
        };
        ToolTip.SetTip(swatch, tip);
        swatch.PointerPressed += (_, _) => { value = hex; Refresh(); Changed?.Invoke(); };
        swatches.Add((hex, swatch));
        Children.Add(swatch);
    }

    private void Refresh()
    {
        foreach (var (hex, swatch) in swatches)
            swatch.BorderBrush = hex == value ? Ring : (hex == null ? Brush.Parse("#C9D3E3") : Brushes.Transparent);
    }

    /// <summary>Picks a colour as if the user clicked it (used by tests).</summary>
    public void Pick(string? hex) { value = ConnectionColors.Normalize(hex); Refresh(); Changed?.Invoke(); }
}
