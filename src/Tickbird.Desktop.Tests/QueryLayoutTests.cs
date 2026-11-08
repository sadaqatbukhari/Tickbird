using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

/// <summary>Resizing the split between the query editor and the results must never hide the editor's scrollbar.</summary>
public class QueryLayoutTests
{
    private static string LongQuery() => string.Join("\n", Enumerable.Range(1, 60).Select(i => $"-- line {i}\nSELECT {i} FROM cpu"));

    private static (Window Window, QueryView View, Grid Split) Open(double height)
    {
        var view = new QueryView { Client = null!, Database = "metrics", EditorText = LongQuery() };
        var window = new Window { Width = 900, Height = height, Content = view };
        window.Show();
        Layout(window);
        return (window, view, Assert.IsType<Grid>(view.Content));
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static ScrollBar VerticalBar(QueryView view) =>
        view.Editor.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);

    [AvaloniaTheory]
    [InlineData(600, 40)]
    [InlineData(600, 80)]
    [InlineData(600, 200)]
    [InlineData(420, 60)]
    [InlineData(300, 40)]
    public void TheEditor_FitsItsCard_WhateverTheSplit(double windowHeight, double editorRowHeight)
    {
        var (window, view, split) = Open(windowHeight);
        try
        {
            split.RowDefinitions[0].Height = new GridLength(editorRowHeight);       // what dragging the splitter does
            split.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            Layout(window);

            var card = Assert.IsType<Border>(split.Children[0]);
            var editorBottom = view.Editor.TranslatePoint(new Point(0, view.Editor.Bounds.Height), card)!.Value.Y;
            Assert.True(editorBottom <= card.Bounds.Height + 0.5,
                $"the editor ends at {editorBottom} but its card is only {card.Bounds.Height} high, so its scrollbar is cut off");
            Assert.True(VerticalBar(view).IsVisible, "a long query needs a visible vertical scrollbar");
            var barBottom = VerticalBar(view).TranslatePoint(new Point(0, VerticalBar(view).Bounds.Height), card)!.Value.Y;
            Assert.True(barBottom <= card.Bounds.Height + 0.5, "the scrollbar must be inside the card");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheSplitter_CannotSqueezeEitherSideToNothing()
    {
        var (window, _, split) = Open(600);
        try
        {
            Assert.Equal(QueryView.EditorMinHeight, split.RowDefinitions[0].MinHeight); Assert.True(split.RowDefinitions[0].MinHeight >= 60, "the editor keeps room for a few lines");
            Assert.True(split.RowDefinitions[2].MinHeight >= 100, "the results keep room for their header and a few rows");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheQuery_CanBeScrolledToItsLastLine()
    {
        var (window, view, split) = Open(500);
        try
        {
            split.RowDefinitions[0].Height = new GridLength(90);
            Layout(window);
            view.Editor.ScrollToLine(view.Editor.LineCount);
            Layout(window);
            Assert.True(view.Editor.VerticalOffset > 0, "scrolling must move the view");
            Assert.True(view.Editor.ExtentHeight > view.Editor.ViewportHeight);
        }
        finally { window.Close(); }
    }
}
