using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Desktop.Views.Requests;

namespace Tickbird.Desktop.Tests;

/// <summary>The Insert, Edit and Delete buttons of a result: shown when the rows can be edited, for the operations the database supports.</summary>
public class RecordButtonsTests
{
    private static ResultSeries Result() => new("result", new[] { "id", "host" }, new Dictionary<string, string>(),
        new List<IList<object>> { new List<object> { 1L, "a" }, new List<object> { 2L, "b" } });

    private static ResultsView View(DatabaseKind kind, string? query)
    {
        var connection = new DatabaseConnection("1", "c", "localhost", 5432, "u", "p", false, "db") { Kind = kind };
        var view = new ResultsView { Client = DatabaseClientFactory.Create(connection), Database = "db", Query = query };
        view.Show(Result());
        return view;
    }

    private static Button Find(ResultsView view, string name) => view.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void APlainSelect_OffersAllThreeOperations_AndEditAndDeleteNeedARow()
    {
        var view = View(DatabaseKind.TimescaleDb, "SELECT * FROM cpu LIMIT 10");
        Assert.Equal("cpu", view.EditableTable);
        Assert.True(Find(view, "RecordInsert").IsVisible && Find(view, "RecordEdit").IsVisible && Find(view, "RecordDelete").IsVisible);
        Assert.False(Find(view, "RecordEdit").IsEnabled);
        Assert.False(Find(view, "RecordDelete").IsEnabled);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        view.ResultsGrid.SelectedIndex = 1;
        view.UpdateRecordButtons();
        Assert.True(Find(view, "RecordEdit").IsEnabled);
        Assert.True(Find(view, "RecordDelete").IsEnabled);
    }

    [AvaloniaFact]
    public void ASchemaQualifiedName_IsKept_ForTheSqlDatabases()
    {
        Assert.Equal("doc.events", View(DatabaseKind.CrateDb, "SELECT * FROM \"doc\".\"events\"").EditableTable);
    }

    [AvaloniaTheory]
    [InlineData("SELECT count(*) FROM cpu")]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id")]
    [InlineData("SHOW TABLES")]
    [InlineData(null)]
    public void AQueryThatIsNotAPlainSelect_HasNoEditButtons(string? query)
    {
        var view = View(DatabaseKind.TimescaleDb, query);
        Assert.Null(view.EditableTable);
        Assert.False(view.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Children.OfType<Button>().Any(b => b.Name == "RecordInsert")).IsVisible);
    }

    [AvaloniaFact]
    public void InfluxDb3_CannotDelete_ButCanInsertAndEdit()
    {
        var view = View(DatabaseKind.InfluxDb3, "SELECT * FROM cpu");
        Assert.True(Find(view, "RecordInsert").IsVisible);
        Assert.True(Find(view, "RecordEdit").IsVisible);
        Assert.False(Find(view, "RecordDelete").IsVisible);
    }

    [AvaloniaFact]
    public void InfluxQl_WithARetentionPolicyInTheName_IsNotEditable()
    {
        Assert.Equal("cpu", View(DatabaseKind.InfluxDb1, "SELECT * FROM \"cpu\" WHERE time > now() - 5m").EditableTable);
        Assert.Null(View(DatabaseKind.InfluxDb1, "SELECT * FROM \"autogen\".\"cpu\"").EditableTable);
    }
}
