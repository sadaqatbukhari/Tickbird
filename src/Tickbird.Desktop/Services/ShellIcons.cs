using Avalonia;
using Avalonia.Media;

namespace Tickbird.Desktop.Services;

/// <summary>Resolution-independent outline icons for the desktop shell.</summary>
internal static class ShellIcons
{
    private const string Database = "M4,5 C4,1 20,1 20,5 C20,9 4,9 4,5 M4,5 L4,19 C4,23 20,23 20,19 L20,5 M4,12 C4,16 20,16 20,12";
    private const string Document = "M5,2 L15,2 L20,7 L20,22 L5,22 Z M15,2 L15,7 L20,7";
    private const string Table = "M3,3 L21,3 L21,21 L3,21 Z M3,8 L21,8 M3,13 L21,13 M3,17 L21,17 M9,8 L9,21 M15,8 L15,21";
    private const string Clock = "M12,2 A10,10 0 1 1 11.99,2 M12,6 L12,12 L17,15";
    private const string Folder = "M2,6 C2,5 3,4 4,4 L9,4 L11,7 L22,7 C23,7 24,8 24,9 L24,20 C24,21 23,22 22,22 L4,22 C3,22 2,21 2,20 Z M2,11 L24,11";
    private const string List = "M3,2 L21,2 L21,22 L3,22 Z M6,6 L7,6 M10,6 L18,6 M6,11 L7,11 M10,11 L18,11 M6,16 L7,16 M10,16 L18,16";
    private static readonly Dictionary<string, IImage> Cache = new();
    private static readonly IBrush Blue = Brush.Parse("#1a65a7");
    private static readonly IBrush Slate = Brush.Parse("#1a65a7");

    public static IImage? Get(string name)
    {
        if (Cache.TryGetValue(name, out var image)) return image;
        if (name is "Comment" or "Uncomment" or "NewQuery")
        {
            image = new DrawingImage(Custom(name));
            Cache[name] = image;
            return image;
        }
        var path = name switch
        {
            "Connection" or "Database" or "CreateDatabase" or "ContinuousQuery" or "BackFill" or "DropDatabase" => Database,
            "Measurement" or "FieldKeys" or "DropMeasurement" => Table,
            "EditConnection" or "Disconnect" or "RunQuery" or "TagValues" => Document,
            "RetentionPolicy" => Clock,
            "Group" => Folder,
            "Diagnostics" or "ShowQueries" or "Series" or "DropSeries" => List,
            "Users" => "M9,3 A4,4 0 1 1 8.99,3 M2,21 L2,18 C2,11 16,11 16,18 L16,21 Z M16,4 C22,3 23,12 17,12 M18,14 C23,14 23,18 23,21 L19,21",
            "Stats" => "M2,21 L22,21 M5,21 L5,12 L8,12 L8,21 M11,21 L11,4 L14,4 L14,21 M17,21 L17,9 L20,9 L20,21",
            "Refresh" => "M21,9 C18,0 5,1 3,10 M21,3 L21,9 L15,9 M3,15 C6,24 19,23 21,14 M3,21 L3,15 L9,15",
            "StopQuery" => "M12,2 A10,10 0 1 1 11.99,2 M8,8 L16,8 L16,16 L8,16 Z",
            "History" => "M3,12 A9,9 0 1 0 6,5 M3,3 L3,8 L8,8 M12,7 L12,12 L16,14",
            "TagKeys" => "M9,15 L15,9 M9,17 L7,19 C2,24 -1,17 3,13 L7,9 C9,7 12,7 14,9 M15,7 L17,5 C22,0 25,7 21,11 L17,15 C15,17 12,17 10,15",
            _ => null
        };
        if (path == null) return Icons.Get(name);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, 26, 26)) });
        var muted = name is "EditConnection" or "Disconnect" or "RetentionPolicy" or "Diagnostics" or "ShowQueries" or "Series" or "DropSeries";
        group.Children.Add(Outline(path, name == "Refresh" ? Brush.Parse("#20A038") : name == "StopQuery" ? Brush.Parse("#D81730") : muted ? Slate : Blue));
        switch (name)
        {
            case "Connection": Badge(group, "M18,17 A4,4 0 1 1 17.99,17", "#20A038", true); break;
            case "CreateDatabase": Badge(group, "M20,14 L20,24 M15,19 L25,19", "#20A038"); break;
            case "Disconnect":
            case "DropDatabase":
            case "DropMeasurement":
            case "DropSeries": Badge(group, "M16,16 L24,24 M24,16 L16,24", "#EF2525"); break;
            case "ContinuousQuery": Badge(group, "M19,12 L24,17 L19,22 M15,17 L24,17", "#20A038"); break;
            case "BackFill": Badge(group, "M19,14 L24,19 L19,24 M15,19 L24,19", "#F58A17"); break;
            case "EditConnection": Badge(group, "M12,21 L21,12 L24,15 L15,24 L11,25 Z", "#1a65a7"); break;
            case "RunQuery":
            case "ShowQueries": Badge(group, "M17,15 L25,19 L17,23 Z", "#20A038", true); break;
            case "TagValues": Badge(group, "M16,14 A5,5 0 1 1 15.99,14 M20,22 L25,26", "#1a65a7"); break;
        }
        image = new DrawingImage(group);
        Cache[name] = image;
        return image;
    }

    private static DrawingGroup Custom(string name)
    {
        var gray = Brush.Parse("#3a3a3a");
        var green = Brush.Parse("#108010");
        var blue = Brush.Parse("#1a6bc4");
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(0, 0, 26, 26)) });
        GeometryDrawing Bars(string path, IBrush brush) => new() { Geometry = Geometry.Parse(path), Pen = new Pen(brush, 2.4, lineCap: PenLineCap.Flat) };
        switch (name)
        {
            case "Comment":
                group.Children.Add(Bars("M3,3.5 L22,3.5 M7,13.5 L22,13.5 M7,23.5 L22,23.5", gray));
                group.Children.Add(Bars("M7,8.5 L22,8.5 M7,18.5 L22,18.5", green));
                break;
            case "Uncomment":
                group.Children.Add(Bars("M10,6 L24,6 M10,10.5 L24,10.5 M10,15 L24,15 M10,19.5 L24,19.5 M10,24 L24,24", gray));
                Badge(group, "M3,8 L8,3 M3,8 L8,13 M4,8 L11,8 C19,8 19,17 8,17", "#1a6bc4");
                break;
            default: // NewQuery: a document with a database cylinder at its bottom-left corner
                group.Children.Add(new GeometryDrawing
                {
                    Geometry = Geometry.Parse("M6,2 L16,2 L21,7 L21,22 L6,22 Z M16,2 L16,7 L21,7"),
                    Brush = Brush.Parse("#eef1f6"), Pen = new Pen(gray, 1.6, lineJoin: PenLineJoin.Round)
                });
                group.Children.Add(Bars("M10,10 L18,10 M10,13.5 L18,13.5 M14,17 L18,17", gray));
                var cylinder = Geometry.Parse("M2,17 C2,14 12,14 12,17 L12,22 C12,25.5 2,25.5 2,22 Z");
                group.Children.Add(new GeometryDrawing { Geometry = cylinder, Brush = Brushes.White, Pen = new Pen(Brushes.White, 4.5, lineJoin: PenLineJoin.Round) });
                group.Children.Add(new GeometryDrawing { Geometry = cylinder, Brush = gray, Pen = new Pen(gray, 1.2, lineJoin: PenLineJoin.Round) });
                group.Children.Add(new GeometryDrawing { Geometry = Geometry.Parse("M2,17 C2,20 12,20 12,17"), Pen = new Pen(Brushes.White, 1.2) });
                break;
        }
        return group;
    }

    private static GeometryDrawing Outline(string path, IBrush brush) => new()
    {
        Geometry = Geometry.Parse(path),
        Pen = new Pen(brush, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
    };

    private static void Badge(DrawingGroup group, string path, string color, bool fill = false)
    {
        var drawing = Outline(path, Brush.Parse(color));
        // A white halo keeps action badges readable where they overlap the base icon.
        group.Children.Add(new GeometryDrawing { Geometry = drawing.Geometry, Pen = new Pen(Brushes.White, 4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Brush = fill ? Brushes.White : null });
        if (fill) drawing.Brush = Brush.Parse(color);
        group.Children.Add(drawing);
    }
}
