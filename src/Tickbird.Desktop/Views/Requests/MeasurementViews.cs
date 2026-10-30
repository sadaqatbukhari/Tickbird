using Avalonia.Controls;
using Avalonia.Layout;
using Tickbird.Core.Data;
using Tickbird.Core.Providers;
using Tickbird.Core.Navigation;

namespace Tickbird.Desktop.Views.Requests;

public abstract class MeasurementView : ListRequestView
{
    public string Measurement { get; set; } = "";
}

public sealed class SeriesView : MeasurementView
{
    public override string ExportFileNameStem => "series";

    protected override async Task<TableData> LoadAsync()
    {
        var names = await Client.GetSeriesNamesAsync(Database, Measurement);
        return SeriesTable.Parse(Measurement, names ?? Enumerable.Empty<string>());
    }
}

public sealed class TagKeysView : MeasurementView
{
    public override string ExportFileNameStem => "tag_keys";

    protected override async Task<TableData> LoadAsync()
    {
        var keys = (await Client.GetTagKeysAsync(Database, Measurement) ?? Enumerable.Empty<string>()).ToList();
        return new TableData(new[] { "tagKey" }, keys.Select(k => (IReadOnlyList<string?>)new[] { k }).ToList());
    }
}

public sealed class TagValuesView : MeasurementView
{
    private readonly ComboBox tagKeys = new() { MinWidth = 180 };
    private bool loadingKeys;

    public TagValuesView()
    {
        Header.Children.Add(new TextBlock { Text = "Tag key:", VerticalAlignment = VerticalAlignment.Center });
        Header.Children.Add(tagKeys);
        tagKeys.SelectionChanged += async (_, _) =>
        {
            if (!loadingKeys && tagKeys.SelectedItem != null) await Guard(ExecuteAsync);
        };
    }

    public override string ExportFileNameStem => "tag_values";

    protected override async Task<TableData> LoadAsync()
    {
        if (tagKeys.ItemCount == 0)
        {
            var keys = (await Client.GetTagKeysAsync(Database, Measurement) ?? Enumerable.Empty<string>()).ToList();
            loadingKeys = true;
            tagKeys.ItemsSource = keys;
            tagKeys.SelectedIndex = keys.Count > 0 ? 0 : -1;
            loadingKeys = false;
        }
        var selected = tagKeys.SelectedItem as string;
        if (selected == null) return new TableData(new[] { "tagValue" }, new List<IReadOnlyList<string?>>());
        var values = await Client.GetTagValuesAsync(Database, Measurement, selected);
        return new TableData(new[] { "tagValue" },
            (values ?? Enumerable.Empty<TagValue>()).Select(v => (IReadOnlyList<string?>)new[] { v.Value }).ToList());
    }
}

/// <summary>Field keys with the retention policy dropdown (InfluxDB 1.x only).</summary>
public sealed class FieldKeysView : MeasurementView
{
    private readonly ComboBox policies = new() { MinWidth = 200 };
    private readonly StackPanel policyPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool policiesLoaded;
    private bool updatingPolicies;

    public FieldKeysView()
    {
        policyPanel.Children.Add(new TextBlock { Text = "Retention policy:", VerticalAlignment = VerticalAlignment.Center });
        policyPanel.Children.Add(policies);
        Header.Children.Insert(0, policyPanel);
        policies.SelectionChanged += async (_, _) =>
        {
            if (!updatingPolicies && policiesLoaded && policies.SelectedItem != null) await Guard(ExecuteAsync);
        };
    }

    public override string ExportFileNameStem => "field_keys";

    private static string Describe(RetentionPolicy p) => p.Name + (p.Default ? " (default)" : string.Empty);

    public override async Task ExecuteAsync()
    {
        await gate.WaitAsync();
        try
        {
            policies.IsEnabled = false;
            await base.ExecuteAsync();
        }
        finally
        {
            policies.IsEnabled = policies.ItemCount > 0;
            gate.Release();
        }
    }

    protected override async Task<TableData> LoadAsync()
    {
        List<FieldKey> fields;
        // Only InfluxDB 1.x keeps fields per retention policy.
        var supportsPolicies = Client.Connection.Kind == DatabaseKind.InfluxDb1;
        policyPanel.IsVisible = supportsPolicies;
        if (!supportsPolicies)
        {
            fields = (await Client.GetFieldKeysAsync(Database, Measurement)).ToList();
        }
        else if (!policiesLoaded)
        {
            var ordered = RetentionPolicyRules.OrderForFields(await Client.GetRetentionPoliciesAsync(Database));
            fields = new List<FieldKey>();
            var selected = ordered.FirstOrDefault();
            // Select the default policy when it has fields, otherwise the first policy that does.
            foreach (var policy in ordered)
            {
                var found = (await Client.GetFieldKeysAsync(Database, Measurement, policy.Name)).ToList();
                if (found.Count == 0) continue;
                selected = policy;
                fields = found;
                break;
            }
            updatingPolicies = true;
            policies.ItemsSource = ordered;
            policies.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RetentionPolicy>(
                (p, _) => new TextBlock { Text = p == null ? "" : Describe(p) });
            policies.SelectedItem = selected;
            updatingPolicies = false;
            policiesLoaded = true;
        }
        else if (policies.SelectedItem is RetentionPolicy policy)
        {
            fields = (await Client.GetFieldKeysAsync(Database, Measurement, policy.Name)).ToList();
        }
        else fields = new List<FieldKey>();

        return new TableData(new[] { "fieldKey", "fieldType" },
            fields.Select(f => (IReadOnlyList<string?>)new[] { f.Name, f.Type }).ToList());
    }
}
