using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tickbird.Desktop.Services;

/// <summary>Presentation for the connection editor; all actions remain owned by TickbirdDialogs.</summary>
internal static class ConnectionDialogLayout
{
    private static readonly IBrush Ink = Brush.Parse("#17233F");
    private static readonly IBrush Muted = Brush.Parse("#7284A5");
    private static readonly IBrush Blue = Brush.Parse("#176FC1");

    internal static void Apply(FormDialog dialog, bool creating, TextBox name, Control group,
        ComboBox server, TextBox host, NumericUpDown port, TextBox database, TextBox username,
        TextBlock usernameLabel, TextBox secret, TextBlock secretLabel, CheckBox useSsl)
    {
        dialog.Width = 520;
        dialog.Background = Brushes.White;
        dialog.Foreground = Ink;
        if (Icons.Get("Connection") is { } icon) dialog.Icon = new WindowIcon(icon);
        dialog.Classes.Add("connectionEditor");
        foreach (var control in new Control[] { name, group, server, host, port, database, username, secret, useSsl })
            if (control.Parent is Panel parent) parent.Children.Remove(control);
        AddStyle(dialog, "TextBox", (TextBox.ForegroundProperty, Ink), (TextBox.FontSizeProperty, 12d),
            (TextBox.MinHeightProperty, 32d), (TextBox.BorderBrushProperty, Brush.Parse("#D4DFEC")),
            (TextBox.CornerRadiusProperty, new CornerRadius(4)), (TextBox.PaddingProperty, new Thickness(10, 6)));
        AddStyle(dialog, "ComboBox", (ComboBox.ForegroundProperty, Ink), (ComboBox.FontSizeProperty, 12d),
            (ComboBox.MinHeightProperty, 32d), (ComboBox.BorderBrushProperty, Brush.Parse("#D4DFEC")));
        var footerButtons = dialog.GetLogicalDescendants().OfType<Button>().ToList();
        foreach (var button in footerButtons) button.Classes.Add("editorAction");
        var save = footerButtons.First(b => b.IsDefault);
        save.Content = creating ? "Create connection" : "Save connection";
        save.Classes.Add("primary");
        if (save.Parent is Panel actions)
        {
            actions.Children.Remove(save);
            actions.Children.Add(save);
        }

        database.Watermark = "Enter database name";
        username.Watermark = "Enter username";
        secret.Watermark = "Enter password";
        var eye = new Button
        {
            Name = "PasswordVisibilityButton", Width = 32, Height = 28, MinHeight = 0,
            Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = Icon("M2,8 C6,2 12,2 16,8 C12,14 6,14 2,8 Z M11,8 A2,2 0 1 1 7,8 A2,2 0 1 1 11,8", 16, Muted)
        };
        ToolTip.SetTip(eye, "Show password");
        eye.Click += (_, _) =>
        {
            secret.PasswordChar = secret.PasswordChar == '\0' ? '•' : '\0';
            ToolTip.SetTip(eye, secret.PasswordChar == '\0' ? "Hide password" : "Show password");
        };
        secret.Name = "ConnectionSecret";
        secret.InnerRightContent = eye;
        host.Name = "ConnectionHost";
        port.Name = "ConnectionPort";
        port.FontSize = 12;
        port.Foreground = Ink;
        port.MinHeight = 32;

        var general = Fields();
        AddRow(general, Label("Name"), name);
        AddRow(general, Label("Group"), group);

        var serverFields = Fields();
        AddRow(serverFields, Label("Server"), server);
        var address = new Grid { ColumnDefinitions = new ColumnDefinitions("*,132"), ColumnSpacing = 10 };
        Grid.SetColumn(host, 0);
        Grid.SetRow(host, 0);
        address.Children.Add(host);
        var portField = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = "Port", FontSize = 11, Foreground = Ink }, port } };
        Grid.SetColumn(portField, 1);
        address.Children.Add(portField);
        host.VerticalAlignment = VerticalAlignment.Bottom;
        AddRow(serverFields, Label("Host"), address);
        AddRow(serverFields, Label("Database"), database);

        var auth = Fields();
        usernameLabel.Text = "Username";
        usernameLabel.FontSize = secretLabel.FontSize = 12;
        usernameLabel.Foreground = secretLabel.Foreground = Ink;
        AddRow(auth, usernameLabel, username);
        AddRow(auth, secretLabel, secret);

        var ssl = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, Margin = new Thickness(0, 16, 0, 0) };
        var sslIcon = Icon("M4,9 L4,5 C4,0 12,0 12,5 L12,9 M2,9 L14,9 L14,19 L2,19 Z", 17, Ink);
        ToolTip.SetTip(sslIcon, "Secure connection (TLS)");
        ssl.Children.Add(sslIcon);
        var sslText = new StackPanel { Spacing = 2, Children =
        {
            new TextBlock { Text = "Use SSL", FontSize = 12, Foreground = Ink },
            new TextBlock { Text = "Encrypt the connection with TLS", FontSize = 10, Foreground = Muted }
        } };
        Grid.SetColumn(sslText, 1);
        ssl.Children.Add(sslText);
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };
        toggle.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = useSsl, Mode = BindingMode.TwoWay });
        Grid.SetColumn(toggle, 2);
        ssl.Children.Add(toggle);

        var generalHeading = Section("GENERAL");
        generalHeading.BorderThickness = new Thickness(0);
        generalHeading.Margin = new Thickness(0, 0, 0, 12);
        generalHeading.Padding = new Thickness(0);
        var body = new StackPanel { Margin = new Thickness(24, 18, 24, 20), Children =
        {
            generalHeading, general, Section("SERVER"), serverFields,
            Section("AUTHENTICATION"), auth, ssl
        } };
        dialog.SetBody(body);
    }

    internal static object ActionContent(string text, string geometry)
        => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children =
        {
            Icon(geometry, 14, Blue), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
        } };

    private static Control Icon(string geometry, double size, IBrush color) => new Avalonia.Controls.Shapes.Path
    {
        Data = Geometry.Parse(geometry), Stroke = color, StrokeThickness = 1.6,
        Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private static Grid Fields() => new() { ColumnDefinitions = new ColumnDefinitions("58,*"), ColumnSpacing = 10, RowSpacing = 10 };
    private static void AddRow(Grid grid, TextBlock label, Control control)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(label, row);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Children.Add(label);
        grid.Children.Add(control);
    }

    private static Border Section(string title) => new()
    {
        BorderBrush = TickbirdTheme.Line, BorderThickness = new Thickness(0, 1, 0, 0),
        Margin = new Thickness(0, 16, 0, 10), Padding = new Thickness(0, 12, 0, 0),
        Child = new TextBlock { Text = title, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Muted }
    };

    private static void AddStyle(Window window, string selector, params (AvaloniaProperty Property, object Value)[] setters)
    {
        var style = new Style(s => selector switch
        {
            "TextBox" => s.Is<TextBox>(),
            "ComboBox" => s.Is<ComboBox>(),
            "Button.primary" => s.Is<Button>().Class("primary"),
            _ => s.Is<Button>().Class("editorAction")
        });
        foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
        window.Styles.Add(style);
    }
}
