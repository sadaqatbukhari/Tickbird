using System.Globalization;
using System.Text.RegularExpressions;
using Tickbird.Core.Charting;
using Tickbird.Core.Data;

namespace Tickbird.Core.Mapping;

/// <summary>How the map colors its lines.</summary>
public enum MapColorMode
{
    /// <summary>One color per track.</summary>
    Single,
    /// <summary>One color per distinct value of the color column (status, mode, a tag...).</summary>
    Categories,
    /// <summary>Numeric color column split into equal value ranges (speed, load...).</summary>
    Ranges,
    /// <summary>The color column already holds colors such as #FF8800.</summary>
    Colors,
    /// <summary>Ordered rules over any fields (state = work and speed &gt; 3 → yellow); the first match wins.</summary>
    Rules
}

public enum MapConditionOperator
{
    Equals,
    NotEquals,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Contains,
    IsEmpty,
    IsNotEmpty
}

/// <summary>One test on one field, such as speed &gt; 3 or state = work.</summary>
public sealed class MapCondition
{
    public string? Column { get; set; }
    public MapConditionOperator Operator { get; set; }
    public string? Value { get; set; }

    /// <summary>A condition with no column is still being edited and is ignored.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Column);

    public static string Symbol(MapConditionOperator op) => op switch
    {
        MapConditionOperator.NotEquals => "≠",
        MapConditionOperator.Greater => ">",
        MapConditionOperator.GreaterOrEqual => "≥",
        MapConditionOperator.Less => "<",
        MapConditionOperator.LessOrEqual => "≤",
        MapConditionOperator.Contains => "contains",
        MapConditionOperator.IsEmpty => "is empty",
        MapConditionOperator.IsNotEmpty => "is not empty",
        _ => "="
    };

    public static bool NeedsValue(MapConditionOperator op) => op is not (MapConditionOperator.IsEmpty or MapConditionOperator.IsNotEmpty);

    public string Describe() => NeedsValue(Operator) ? $"{Column} {Symbol(Operator)} {Value}" : $"{Column} {Symbol(Operator)}";

    /// <summary>
    /// Tests a cell. Numbers (and numeric strings) compare as numbers when the value is a number;
    /// everything else compares as text, ignoring case.
    /// </summary>
    public bool Matches(object? cell)
    {
        var text = cell switch
        {
            null => null,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => cell.ToString()
        };
        var empty = string.IsNullOrEmpty(text);
        if (Operator == MapConditionOperator.IsEmpty) return empty;
        if (Operator == MapConditionOperator.IsNotEmpty) return !empty;

        var target = Value?.Trim() ?? "";
        if (cell == null) return Operator == MapConditionOperator.NotEquals && target.Length > 0;
        var number = cell is bool ? double.NaN : MapTrackBuilder.ToCoordinate(cell);
        var targetNumber = double.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN;
        var numeric = !double.IsNaN(number) && !double.IsNaN(targetNumber);
        var equal = numeric ? number == targetNumber : string.Equals(text, target, StringComparison.OrdinalIgnoreCase);
        return Operator switch
        {
            MapConditionOperator.Equals => equal,
            MapConditionOperator.NotEquals => !equal,
            MapConditionOperator.Greater => numeric ? number > targetNumber : string.Compare(text, target, StringComparison.OrdinalIgnoreCase) > 0,
            MapConditionOperator.GreaterOrEqual => numeric ? number >= targetNumber : string.Compare(text, target, StringComparison.OrdinalIgnoreCase) >= 0,
            MapConditionOperator.Less => numeric ? number < targetNumber : string.Compare(text, target, StringComparison.OrdinalIgnoreCase) < 0,
            MapConditionOperator.LessOrEqual => numeric ? number <= targetNumber : string.Compare(text, target, StringComparison.OrdinalIgnoreCase) <= 0,
            MapConditionOperator.Contains => text!.Contains(target, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

/// <summary>How a layer is drawn over the track.</summary>
public enum MapLayerStyle
{
    /// <summary>A colored line on top of the track.</summary>
    Line,
    /// <summary>A wide colored band under the track (the track's own color stays visible).</summary>
    Halo,
    /// <summary>A dashed line on top of the track.</summary>
    Dashed,
    /// <summary>A dot at each matching point (events such as a bunker unload).</summary>
    Points
}

/// <summary>
/// An overlay on the track where its conditions hold (operatingmode = 2, depthguidance = on, bunkerunload = 1...).
/// Any number of layers can show at once; each point can be in several layers.
/// </summary>
public sealed class MapLayer
{
    public string? Name { get; set; }
    public List<MapCondition> Conditions { get; set; } = new();
    public string Color { get; set; } = "#FF9100";
    public MapLayerStyle Style { get; set; } = MapLayerStyle.Line;
    public double Width { get; set; } = 3;
    public bool Visible { get; set; } = true;

    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name.Trim();
        var parts = Conditions.Where(c => c.IsComplete).Select(c => c.Describe()).ToList();
        return parts.Count > 0 ? string.Join(" and ", parts) : "(no conditions)";
    }

    public MapLayer Copy() => new()
    {
        Name = Name, Color = Color, Style = Style, Width = Width, Visible = Visible,
        Conditions = Conditions.Select(c => new MapCondition { Column = c.Column, Operator = c.Operator, Value = c.Value }).ToList()
    };
}

/// <summary>A color for the points that meet all of its conditions (AND). A rule with no conditions matches every point.</summary>
public sealed class MapColorRule
{
    public List<MapCondition> Conditions { get; set; } = new();
    public string Color { get; set; } = "#2979FF";
    /// <summary>Legend text; empty shows the conditions.</summary>
    public string? Label { get; set; }

    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Label)) return Label.Trim();
        var parts = Conditions.Where(c => c.IsComplete).Select(c => c.Describe()).ToList();
        return parts.Count > 0 ? string.Join(" and ", parts) : "All points";
    }
}

/// <summary>Which result columns are bound to the map.</summary>
public sealed class MapOptions
{
    public string? LatitudeColumn { get; init; }
    public string? LongitudeColumn { get; init; }
    /// <summary>Column whose value picks the line color; null for one color per track.</summary>
    public string? ColorColumn { get; init; }
    /// <summary>Forces a color mode; null chooses from the column's values.</summary>
    public MapColorMode? ColorMode { get; init; }
    public int RangeCount { get; init; } = 5;
    /// <summary>Text column that splits the rows into separate tracks (one line per vehicle, say).</summary>
    public string? SplitBy { get; init; }
    public int MaxCategories { get; init; } = 24;
    /// <summary>Color rules, used when <see cref="ColorMode"/> is <see cref="MapColorMode.Rules"/>.</summary>
    public IReadOnlyList<MapColorRule>? Rules { get; init; }
    /// <summary>Overlay layers (at most <see cref="MapTrackBuilder.MaxLayers"/>).</summary>
    public IReadOnlyList<MapLayer>? Layers { get; init; }
    /// <summary>Color of points that match no rule.</summary>
    public string OtherColor { get; init; } = "#9E9E9E";
}

/// <summary>One GPS fix. <see cref="Legend"/> indexes <see cref="MapData.Legend"/>.</summary>
/// <summary>One GPS fix. <see cref="Legend"/> indexes <see cref="MapData.Legend"/>; bit n of <see cref="Layers"/> is set when the point is in layer n.</summary>
public readonly record struct MapPoint(double Latitude, double Longitude, int Legend, DateTime? Time, string? Value, ulong Layers = 0)
{
    public bool InLayer(int layer) => (Layers & (1UL << layer)) != 0;
}

public sealed class MapTrack
{
    public MapTrack(string label, MapPoint[] points) { Label = label; Points = points; }
    public string Label { get; }
    public MapPoint[] Points { get; }
}

/// <summary>A color key: label shown in the legend and an optional default color (#RRGGBB).</summary>
public sealed class MapLegendEntry
{
    public MapLegendEntry(string label, string? defaultColor) { Label = label; DefaultColor = defaultColor; }
    public string Label { get; }
    public string? DefaultColor { get; }
    public int Count { get; internal set; }
}

public sealed record MapBounds(double South, double West, double North, double East);

/// <summary>Tracks ready to draw, plus what the map's toolbar offers.</summary>
public sealed class MapData
{
    /// <summary>Every column of the result, for rule conditions.</summary>
    public IReadOnlyList<string> AllColumns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoordinateColumns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ColorColumns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SplitCandidates { get; init; } = Array.Empty<string>();
    public string? LatitudeColumn { get; init; }
    public string? LongitudeColumn { get; init; }
    public string? ColorColumn { get; init; }
    public MapColorMode ColorMode { get; init; }
    public IReadOnlyList<MapTrack> Tracks { get; init; } = Array.Empty<MapTrack>();
    public IReadOnlyList<MapLegendEntry> Legend { get; init; } = Array.Empty<MapLegendEntry>();
    /// <summary>The overlay layers drawn (a snapshot of the options).</summary>
    public IReadOnlyList<MapLayer> Layers { get; init; } = Array.Empty<MapLayer>();
    /// <summary>Number of points in each layer.</summary>
    public IReadOnlyList<int> LayerCounts { get; init; } = Array.Empty<int>();
    public MapBounds? Bounds { get; init; }
    public int PointCount { get; init; }
    /// <summary>Rows without a usable position (null, out of range, or 0,0).</summary>
    public int SkippedRows { get; init; }
    public string? Message { get; init; }
}

/// <summary>Turns query results into map tracks: finds latitude/longitude columns and assigns line colors.</summary>
public static class MapTrackBuilder
{
    private static readonly string[] TimeNames = { "time", "timestamp", "_time" };
    private static readonly string[] LatitudeNames = { "latitude", "lat", "gps_latitude", "gpslatitude", "gps_lat", "position_latitude", "y" };
    private static readonly string[] LongitudeNames = { "longitude", "lon", "lng", "long", "gps_longitude", "gpslongitude", "gps_lon", "gps_lng", "position_longitude", "x" };
    private static readonly Regex HexColor = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);
    private const int SampleRows = 500;
    private const int MaxNumericCategories = 8;

    public static MapData Build(IReadOnlyList<ResultSeries> sources, MapOptions options)
    {
        var columns = new List<string>();
        foreach (var source in sources)
            foreach (var column in source.Columns)
                if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase)) columns.Add(column);

        var timeColumn = columns.FirstOrDefault(c => TimeNames.Contains(c, StringComparer.OrdinalIgnoreCase));
        var coordinateColumns = columns.Where(c => c != timeColumn && IsCoordinateColumn(sources, c)).ToList();
        var textColumns = columns.Where(c => c != timeColumn && IsTextColumn(sources, c)).ToList();

        var lat = Pick(options.LatitudeColumn, coordinateColumns, LatitudeNames, "lat");
        var lon = Pick(options.LongitudeColumn, coordinateColumns.Where(c => !string.Equals(c, lat, StringComparison.OrdinalIgnoreCase)).ToList(),
            LongitudeNames, "lon", "lng");
        var colorColumns = columns.Where(c => c != timeColumn && !Same(c, lat) && !Same(c, lon)).ToList();
        var colorColumn = options.ColorColumn != null ? colorColumns.FirstOrDefault(c => Same(c, options.ColorColumn)) : null;
        var splitBy = options.SplitBy != null ? textColumns.FirstOrDefault(c => Same(c, options.SplitBy)) : null;

        if (lat == null || lon == null)
        {
            return new MapData
            {
                AllColumns = columns, CoordinateColumns = coordinateColumns, ColorColumns = colorColumns, SplitCandidates = textColumns,
                LatitudeColumn = lat, LongitudeColumn = lon,
                Message = coordinateColumns.Count < 2
                    ? "This result has no latitude/longitude columns. Select numeric fields such as lat and lon in the query."
                    : "Choose the Latitude and Longitude columns."
            };
        }

        var rules = options.ColorMode == MapColorMode.Rules && options.Rules is { Count: > 0 } ? options.Rules : null;

        // Rows -> raw points, grouped into tracks. With rules, the "color" of a point is the index of the first rule it meets.
        var showName = sources.Select(s => s.Name ?? "").Distinct().Count() > 1;
        var layers = (options.Layers ?? Array.Empty<MapLayer>()).Take(MaxLayers).ToList();
        var layerCounts = new int[layers.Count];
        var tracks = new List<(string Label, List<(double Lat, double Lon, object? Color, DateTime? Time, ulong Layers)> Points)>();
        var trackIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var source in sources)
        {
            var iLat = IndexOf(source, lat);
            var iLon = IndexOf(source, lon);
            if (iLat < 0 || iLon < 0) continue;
            var iColor = colorColumn != null ? IndexOf(source, colorColumn) : -1;
            var iTime = timeColumn != null ? IndexOf(source, timeColumn) : -1;
            var iSplit = splitBy != null ? IndexOf(source, splitBy) : -1;
            var baseLabel = SourceLabel(source, showName, sources.Count);
            var ruleColumns = rules?.Select(r => ConditionColumns(source, r.Conditions)).ToArray();
            var layerColumns = layers.Select(l => ConditionColumns(source, l.Conditions)).ToArray();
            foreach (var row in source.Values)
            {
                var y = ToCoordinate(Cell(row, iLat));
                var x = ToCoordinate(Cell(row, iLon));
                if (double.IsNaN(y) || double.IsNaN(x) || y < -90 || y > 90 || x < -180 || x > 180 || (y == 0 && x == 0))
                {
                    skipped++;
                    continue;
                }
                var label = baseLabel;
                if (iSplit >= 0)
                {
                    var split = Cell(row, iSplit)?.ToString();
                    label = string.IsNullOrEmpty(label) ? split ?? "(none)" : $"{label} · {split ?? "(none)"}";
                }
                if (string.IsNullOrEmpty(label)) label = "Track";
                if (!trackIndex.TryGetValue(label, out var t))
                {
                    t = tracks.Count;
                    trackIndex[label] = t;
                    tracks.Add((label, new List<(double Lat, double Lon, object? Color, DateTime? Time, ulong Layers)>()));
                }
                DateTime? time = iTime >= 0 && ChartValues.TryGetUtc(Cell(row, iTime), out var utc) ? utc : null;
                var color = rules != null ? FirstRule(rules, ruleColumns!, row) : iColor >= 0 ? Cell(row, iColor) : null;
                ulong bits = 0;
                for (var l = 0; l < layers.Count; l++)
                {
                    // A layer without a complete condition is still being edited: it matches nothing.
                    if (!layers[l].Conditions.Any(c => c.IsComplete) || !MeetsAll(layers[l].Conditions, layerColumns[l], row)) continue;
                    bits |= 1UL << l;
                    layerCounts[l]++;
                }
                tracks[t].Points.Add((y, x, color, time, bits));
            }
        }

        // Legend and color assignment.
        var legend = new List<MapLegendEntry>();
        var mode = rules != null ? MapColorMode.Rules
            : colorColumn == null ? MapColorMode.Single
            : options.ColorMode is { } forced && forced != MapColorMode.Rules ? forced
            : ChooseMode(tracks.SelectMany(t => t.Points).Select(p => p.Color));
        if (mode is MapColorMode.Single or MapColorMode.Rules) colorColumn = null;
        Func<object?, (int Legend, string? Text)> classify;
        switch (mode)
        {
            case MapColorMode.Rules:
                foreach (var rule in rules!) legend.Add(new MapLegendEntry(rule.Describe(), rule.Color));
                legend.Add(new MapLegendEntry("Other", options.OtherColor));
                classify = value => { var i = (int)value!; return (i, legend[i].Label); };
                break;
            case MapColorMode.Ranges:
                classify = RangeClassifier(tracks.SelectMany(t => t.Points).Select(p => p.Color), options.RangeCount, legend);
                break;
            case MapColorMode.Colors:
                classify = CategoryClassifier(legend, options.MaxCategories, colors: true);
                break;
            case MapColorMode.Categories:
                classify = CategoryClassifier(legend, options.MaxCategories, colors: false);
                break;
            default:
                foreach (var track in tracks) legend.Add(new MapLegendEntry(track.Label, null));
                classify = _ => (0, null);
                break;
        }

        var result = new List<MapTrack>();
        double south = 90, north = -90, west = 180, east = -180;
        var count = 0;
        for (var t = 0; t < tracks.Count; t++)
        {
            var raw = tracks[t].Points;
            var points = new MapPoint[raw.Count];
            for (var i = 0; i < raw.Count; i++)
            {
                var p = raw[i];
                var (index, text) = mode == MapColorMode.Single ? (t, null) : classify(p.Color);
                legend[index].Count++;
                points[i] = new MapPoint(p.Lat, p.Lon, index, p.Time, text, p.Layers);
                south = Math.Min(south, p.Lat); north = Math.Max(north, p.Lat);
                west = Math.Min(west, p.Lon); east = Math.Max(east, p.Lon);
            }
            count += points.Length;
            result.Add(new MapTrack(tracks[t].Label, points));
        }

        return new MapData
        {
            AllColumns = columns,
            CoordinateColumns = coordinateColumns,
            ColorColumns = colorColumns,
            SplitCandidates = textColumns,
            LatitudeColumn = lat,
            LongitudeColumn = lon,
            ColorColumn = colorColumn,
            ColorMode = mode,
            Tracks = result,
            Legend = mode == MapColorMode.Single ? legend : Compact(result, legend),
            Layers = layers,
            LayerCounts = layerCounts,
            Bounds = count > 0 ? new MapBounds(south, west, north, east) : null,
            PointCount = count,
            SkippedRows = skipped,
            Message = count == 0 ? $"No rows have a usable position in {lat} / {lon}." : null
        };
    }

    /// <summary>Index of the first rule whose conditions the row meets; rules.Count when none does.</summary>
    private static int FirstRule(IReadOnlyList<MapColorRule> rules, int[][] columns, IList<object> row)
    {
        for (var r = 0; r < rules.Count; r++)
            if (MeetsAll(rules[r].Conditions, columns[r], row)) return r;
        return rules.Count;
    }

    /// <summary>Most layers one map can show (each is a bit of <see cref="MapPoint.Layers"/>).</summary>
    public const int MaxLayers = 64;

    /// <summary>Column index of each condition in this result: -1 when missing, -2 when the condition is incomplete.</summary>
    private static int[] ConditionColumns(ResultSeries source, List<MapCondition> conditions) =>
        conditions.Select(c => c.IsComplete ? IndexOf(source, c.Column!) : -2).ToArray();

    /// <summary>True when the row meets every complete condition (incomplete ones are ignored).</summary>
    private static bool MeetsAll(List<MapCondition> conditions, int[] columns, IList<object> row)
    {
        for (var c = 0; c < conditions.Count; c++)
        {
            var index = columns[c];
            if (index == -2) continue;
            if (!conditions[c].Matches(index >= 0 ? Cell(row, index) : null)) return false;
        }
        return true;
    }

    /// <summary>Distinct values of a column (in order of appearance), for "a layer per value".</summary>
    public static IReadOnlyList<string> DistinctValues(IReadOnlyList<ResultSeries> sources, string column, int max = 24)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var i = IndexOf(source, column);
            if (i < 0) continue;
            foreach (var row in source.Values)
            {
                var value = Cell(row, i);
                if (value == null) continue;
                var text = Text(value);
                if (seen.Add(text)) values.Add(text);
                if (values.Count > max) return values;
            }
        }
        return values;
    }

    /// <summary>Reads a coordinate from a number or a numeric string; anything else is NaN.</summary>
    public static double ToCoordinate(object? value) => value switch
    {
        null or bool => double.NaN,
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        _ => ChartValues.ToDouble(value)
    };

    /// <summary>Picks the color mode a column's values suggest.</summary>
    public static MapColorMode ChooseMode(IEnumerable<object?> values)
    {
        var sample = values.Where(v => v != null).Take(5000).ToList();
        if (sample.Count == 0) return MapColorMode.Categories;
        if (sample.All(v => v is string s && HexColor.IsMatch(s.Trim()))) return MapColorMode.Colors;
        if (sample.All(v => ChartValues.IsNumeric(v) && v is not bool) &&
            sample.Select(v => ChartValues.ToDouble(v)).Distinct().Take(MaxNumericCategories + 1).Count() > MaxNumericCategories)
            return MapColorMode.Ranges;
        return MapColorMode.Categories;
    }

    private static Func<object?, (int, string?)> CategoryClassifier(List<MapLegendEntry> legend, int maxCategories, bool colors)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var other = -1;
        return value =>
        {
            var text = Text(value);
            var key = colors ? text.ToUpperInvariant() : text;
            if (colors && !HexColor.IsMatch(key)) key = "(other)";
            if (index.TryGetValue(key, out var i)) return (i, text);
            if (legend.Count >= Math.Max(2, maxCategories) - 1)
            {
                if (other < 0) { other = legend.Count; legend.Add(new MapLegendEntry("(other values)", "#9E9E9E")); }
                return (other, text);
            }
            i = legend.Count;
            index[key] = i;
            legend.Add(new MapLegendEntry(key, colors && key.StartsWith('#') ? key : null));
            return (i, text);
        };
    }

    private static Func<object?, (int, string?)> RangeClassifier(IEnumerable<object?> values, int rangeCount, List<MapLegendEntry> legend)
    {
        var numbers = values.Select(v => v is bool ? double.NaN : ToCoordinate(v)).Where(d => !double.IsNaN(d) && !double.IsInfinity(d)).ToList();
        var min = numbers.Count > 0 ? numbers.Min() : 0;
        var max = numbers.Count > 0 ? numbers.Max() : 0;
        var n = max > min ? Math.Clamp(rangeCount, 2, 12) : 1;
        var step = n > 1 ? (max - min) / n : 1;
        for (var i = 0; i < n; i++)
        {
            var low = min + step * i;
            var high = i == n - 1 ? max : min + step * (i + 1);
            var label = n == 1 ? ChartValues.FormatNumber(min) : $"{ChartValues.FormatNumber(low)} – {ChartValues.FormatNumber(high)}";
            legend.Add(new MapLegendEntry(label, Gradient(n == 1 ? 0 : i / (double)(n - 1))));
        }
        var missing = -1;
        return value =>
        {
            var d = value is bool ? double.NaN : ToCoordinate(value);
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                if (missing < 0) { missing = legend.Count; legend.Add(new MapLegendEntry("(no value)", "#9E9E9E")); }
                return (missing, value == null ? null : Text(value));
            }
            var i = n == 1 ? 0 : Math.Clamp((int)((d - min) / step), 0, n - 1);
            return (i, ChartValues.FormatNumber(d));
        };
    }

    /// <summary>Green → yellow → red, readable on satellite imagery.</summary>
    public static string Gradient(double t)
    {
        (int R, int G, int B)[] stops = { (0x2E, 0xCC, 0x40), (0xFF, 0xDC, 0x00), (0xFF, 0x41, 0x36) };
        t = Math.Clamp(t, 0, 1) * (stops.Length - 1);
        var i = Math.Min((int)t, stops.Length - 2);
        var f = t - i;
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * f);
        return $"#{Mix(stops[i].R, stops[i + 1].R):X2}{Mix(stops[i].G, stops[i + 1].G):X2}{Mix(stops[i].B, stops[i + 1].B):X2}";
    }

    /// <summary>Drops legend entries no point uses and renumbers the points.</summary>
    private static IReadOnlyList<MapLegendEntry> Compact(List<MapTrack> tracks, List<MapLegendEntry> legend)
    {
        var map = new int[legend.Count];
        var kept = new List<MapLegendEntry>();
        for (var i = 0; i < legend.Count; i++)
        {
            map[i] = legend[i].Count > 0 ? kept.Count : -1;
            if (legend[i].Count > 0) kept.Add(legend[i]);
        }
        foreach (var track in tracks)
            for (var i = 0; i < track.Points.Length; i++)
                track.Points[i] = track.Points[i] with { Legend = map[track.Points[i].Legend] };
        return kept;
    }

    private static string Text(object? value) => value switch
    {
        null => "(empty)",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string? Pick(string? chosen, List<string> candidates, string[] names, params string[] fragments)
    {
        if (chosen != null)
        {
            var match = candidates.FirstOrDefault(c => Same(c, chosen));
            if (match != null) return match;
        }
        foreach (var name in names)
        {
            var match = candidates.FirstOrDefault(c => Same(c, name));
            if (match != null) return match;
        }
        foreach (var fragment in fragments)
        {
            var match = candidates.FirstOrDefault(c => c.Contains(fragment, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private static bool IsCoordinateColumn(IReadOnlyList<ResultSeries> sources, string column)
    {
        var seen = 0;
        foreach (var source in sources)
        {
            var i = IndexOf(source, column);
            if (i < 0) continue;
            foreach (var row in source.Values.Take(SampleRows))
            {
                var value = Cell(row, i);
                if (value == null) continue;
                var d = ToCoordinate(value);
                if (double.IsNaN(d) || d < -180 || d > 180) return false;
                seen++;
            }
        }
        return seen > 0;
    }

    private static bool IsTextColumn(IReadOnlyList<ResultSeries> sources, string column)
    {
        var seen = 0;
        foreach (var source in sources)
        {
            var i = IndexOf(source, column);
            if (i < 0) continue;
            foreach (var row in source.Values.Take(SampleRows))
            {
                var value = Cell(row, i);
                if (value == null) continue;
                if (value is not string s || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
                seen++;
            }
        }
        return seen > 0;
    }

    private static string SourceLabel(ResultSeries source, bool showName, int sourceCount)
    {
        if (sourceCount <= 1) return source.Tags.Count > 0 ? string.Join(", ", source.Tags.Select(t => $"{t.Key}={t.Value}")) : "";
        var parts = source.Tags.Select(t => $"{t.Key}={t.Value}").ToList();
        if (showName && !string.IsNullOrEmpty(source.Name)) parts.Insert(0, source.Name);
        return parts.Count > 0 ? string.Join(", ", parts) : source.Name ?? "";
    }

    private static int IndexOf(ResultSeries source, string column)
    {
        for (var i = 0; i < source.Columns.Count; i++)
            if (Same(source.Columns[i], column)) return i;
        return -1;
    }

    private static object? Cell(IList<object> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

    private static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
