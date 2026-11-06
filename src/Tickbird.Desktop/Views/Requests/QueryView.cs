using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Tickbird.Core.Data;
using Tickbird.Core.Completion;
using Tickbird.Core.History;
using Tickbird.Core.Query;
using Tickbird.Desktop.Services;

namespace Tickbird.Desktop.Views.Requests;

/// <summary>Query editor with completion and result tabs (port of QueryControl).</summary>
public sealed class QueryView : RequestView
{
    private readonly TextEditor editor = new()
    {
        ShowLineNumbers = true,
        // SQL Server Management Studio look: Consolas, black text, teal line numbers.
        FontFamily = new FontFamily("Consolas,Cascadia Mono,Menlo,monospace"),
        FontSize = 13,
        MinHeight = 120,
        Background = Brushes.White,
        Foreground = Brushes.Black,
        LineNumbersForeground = Brush.Parse("#2B91AF"),
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
    };
    private readonly TabControl results = new();
    private readonly TextBlock summary = new()
    {
        Margin = new Thickness(4, 10, 4, 2),
        FontSize = 12,
        Foreground = Brush.Parse("#1a65a7")
    };
    private readonly CompletionProvider completion = new();
    private CompletionWindow? completionWindow;
    private readonly Stopwatch stopwatch = new();
    private readonly SearchPanel searchPanel;
    private readonly Button stopButton = new()
    {
        Content = "■  Stop", IsVisible = false, Padding = new Thickness(10, 2), MinHeight = 26, FontSize = 12,
        Foreground = Brush.Parse("#D81730"), Margin = new Thickness(0, 8, 8, 2), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private readonly Avalonia.Threading.DispatcherTimer runTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CancellationTokenSource? running;
    private bool stopRequested;
    // Grid/Chart choice, chart style, split and hidden fields persist across runs of this query tab.
    private readonly ChartSettings chartSettings = new();

    public QueryView()
    {
        Background = Brushes.White;
        results.Classes.Add("queryResultTabs");
        editor.Options.HighlightCurrentLine = true;
        editor.TextArea.SelectionBrush = Brush.Parse("#ADD6FF");
        editor.TextArea.TextView.CurrentLineBackground = Brushes.White;
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        editor.TextArea.TextView.LineTransformers.Add(new InfluxQlColorizer());
        // Tunnel so Ctrl+Space reaches us before the editor treats it as typing.
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.TextArea.TextEntered += OnTextEntered;
        // Ctrl+F find, Ctrl+H replace, F3 / Shift+F3 next / previous, Esc closes.
        searchPanel = SearchPanel.Install(editor);
        stopButton.Click += (_, _) => CancelQuery();
        ToolTip.SetTip(stopButton, "Stop the running query");
        runTimer.Tick += (_, _) => summary.Text = $"Running query… {stopwatch.Elapsed.TotalSeconds:0.0} s";
        // The list's own focus check is replaced by this one (see EditorCompletionWindow).
        editor.TextArea.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (completionWindow != null && !editor.TextArea.IsKeyboardFocusWithin && !completionWindow.IsKeyboardFocusWithin)
                completionWindow.Close();
        }, Avalonia.Threading.DispatcherPriority.Background);

        var split = new Grid { RowDefinitions = new RowDefinitions("2*,10,3*"), Margin = new Thickness(10) };
        var splitter = new GridSplitter
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        Grid.SetRow(splitter, 1);
        var bottom = new DockPanel();
        var status = new DockPanel();
        DockPanel.SetDock(stopButton, Dock.Left);
        status.Children.Add(stopButton);
        status.Children.Add(summary);
        DockPanel.SetDock(status, Dock.Bottom);
        bottom.Children.Add(status);
        bottom.Children.Add(results);
        var editorCard = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#e3ecf4"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10),
            ClipToBounds = true,
            Child = editor
        };
        var resultsCard = new Border
        {
            Background = Brush.Parse("#F8FBFF"),
            BorderBrush = Brush.Parse("#e3ecf4"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 4, 12, 12),
            Child = bottom
        };
        Grid.SetRow(resultsCard, 2);
        split.Children.Add(editorCard);
        split.Children.Add(splitter);
        split.Children.Add(resultsCard);
        Content = split;
    }

    public string EditorText
    {
        get => editor.Text;
        set { editor.Text = value; savedText = value; }
    }

    private string savedText = "";

    /// <summary>True when the editor text differs from what was last loaded, opened or saved.</summary>
    public bool IsDirty => !string.Equals(editor.Text ?? "", savedText ?? "", StringComparison.Ordinal);

    /// <summary>Loads the whole database schema (shared cache); set by the main window.</summary>
    public Func<Task<IReadOnlyList<InfluxDbMeasurementSchema>>>? SchemaLoader { get; set; }

    /// <summary>Loads the values of a tag in a measurement (shared cache); set by the main window.</summary>
    public Func<string, string, Task<IReadOnlyList<string>>>? TagValueLoader { get; set; }

    /// <summary>The completion source (exposed for tests).</summary>
    public CompletionProvider Completion => completion;

    public TextEditor Editor => editor;

    /// <summary>Chart preferences shared by this tab's results (exposed for tests).</summary>
    public ChartSettings ChartSettings => chartSettings;

    public async Task InitializeIntellisenseAsync()
    {
        summary.Text = "IntelliSense: loading measurements and fields…";
        try
        {
            if (SchemaLoader != null) completion.SetSchema(await SchemaLoader());
            else await completion.LoadMeasurementsAsync(Client, Database);
        }
        catch
        {
            // Query execution must remain available if schema discovery is not permitted.
            try { await completion.LoadMeasurementsAsync(Client, Database); } catch { }
        }
        if (summary.Text?.StartsWith("IntelliSense") == true)
            summary.Text = $"IntelliSense: {completion.Measurements.Count} measurements, {completion.FieldCount} fields, "
                           + $"{completion.TagCount} tags. Press Ctrl+Space for suggestions.";
    }

    private static readonly Dictionary<string, string> lastParameterValues = new(StringComparer.Ordinal);

    /// <summary>Saves the selection, or the whole editor text, as a named snippet.</summary>
    public async Task SaveSnippetAsync()
    {
        var text = QueryText.TextToExecute(editor.SelectedText, editor.Text);
        if (OwnerWindow == null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            await Dialogs.ShowMessageAsync(OwnerWindow, "Save Snippet", "Select or enter a query to save.");
            return;
        }
        var name = await Dialogs.PromptTextAsync(OwnerWindow, "Save Snippet",
            "Name for this snippet. Saving under an existing name replaces that snippet. Use {{name}} in the query for values asked when it runs.", null, ok: "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        TickbirdApp.Current.Snippets.Save(name, text);
        summary.Text = $"Snippet \"{name.Trim()}\" saved.";
    }

    /// <summary>Lets the user pick a snippet and puts it at the caret (replacing the selection).</summary>
    public async Task InsertSnippetAsync()
    {
        if (OwnerWindow == null) return;
        var text = await new SnippetsWindow(TickbirdApp.Current.Snippets).ShowDialog<string?>(OwnerWindow);
        if (text == null) return;
        editor.TextArea.Selection.ReplaceSelectionWithText(text);
        editor.Focus();
    }

    public override async Task ExecuteAsync()
    {
        if (Client == null) throw new InvalidOperationException("No InfluxDB client available.");
        var query = QueryText.TextToExecute(editor.SelectedText, editor.Text);
        if (string.IsNullOrWhiteSpace(query))
        {
            if (OwnerWindow != null) await Dialogs.ShowMessageAsync(OwnerWindow, "Run Query", "Select or enter a query to run.");
            return;
        }
        if (IsRunning) return;
        var parameters = QueryParameters.Find(query);
        if (parameters.Count > 0)
        {
            if (OwnerWindow == null) return;
            var values = await Dialogs.PromptParametersAsync(OwnerWindow, parameters, lastParameterValues);
            if (values == null) return;
            foreach (var pair in values) lastParameterValues[pair.Key] = pair.Value;
            query = QueryParameters.Apply(query, values);
        }
        var label = QueryText.IsAggregate(query) ? "Group" : "Results";
        var timeoutSeconds = TickbirdApp.Current.Settings.QueryTimeoutSeconds;
        using var cancel = new CancellationTokenSource();
        if (timeoutSeconds > 0) cancel.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var entry = new QueryHistoryEntry
        {
            RunAtUtc = DateTime.UtcNow, ConnectionId = Client.Connection.Id, ConnectionName = Client.Connection.Name,
            Database = Database, Query = query
        };
        results.Items.Clear();
        stopwatch.Restart();
        running = cancel;
        stopRequested = false;
        SetRunning(true);
        List<InfluxDbSeries> allResults;
        try
        {
            allResults = ((await Client.QueryAsync(Database, query, cancel.Token)) ?? Enumerable.Empty<InfluxDbSeries>()).ToList();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            stopwatch.Stop();
            var timedOut = !stopRequested;
            entry.Status = timedOut ? QueryRunStatus.TimedOut : QueryRunStatus.Cancelled;
            entry.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            Record(entry);
            summary.Text = timedOut
                ? $"Query stopped after the {FormatTimeout(timeoutSeconds)} query timeout (Settings › Query Timeout)."
                : $"Query cancelled after {stopwatch.Elapsed.TotalSeconds:0.0} s.";
            return;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            entry.Status = QueryRunStatus.Failed;
            entry.Error = ex.Message;
            entry.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
            Record(entry);
            summary.Text = $"Query failed after {stopwatch.Elapsed.TotalMilliseconds:0} ms.";
            throw;
        }
        finally
        {
            running = null;
            SetRunning(false);
        }
        stopwatch.Stop();
        var total = 0;
        var count = 0;
        foreach (var series in allResults)
        {
            var view = new ResultsView { Client = Client, Database = Database, ChartSettings = chartSettings, AllResults = allResults };
            total += view.Show(series);
            var heading = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new Image { Source = ShellIcons.Get("Measurement"), Width = 15, Height = 15 });
            heading.Children.Add(new TextBlock { Text = $"{label} {++count}", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            results.Items.Add(new TabItem { Header = heading, Content = view });
        }
        summary.Text = $"results: {total}, response time: {stopwatch.Elapsed.TotalMilliseconds:0} ms";
        entry.Status = QueryRunStatus.Succeeded;
        entry.Rows = total;
        entry.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        Record(entry);
    }

    /// <summary>True while a query from this editor is running.</summary>
    public bool IsRunning => running != null;

    /// <summary>Raised when a query starts or finishes (the main window enables Stop).</summary>
    public event EventHandler? RunningChanged;

    /// <summary>Stops the running query (Stop button, toolbar or Query › Stop). The request is closed, which also ends it on an InfluxDB 1.x server.</summary>
    public void CancelQuery()
    {
        if (running == null) return;
        stopRequested = true;
        running.Cancel();
    }

    private void SetRunning(bool on)
    {
        stopButton.IsVisible = on;
        if (on)
        {
            summary.Text = "Running query…";
            runTimer.Start();
        }
        else
        {
            runTimer.Stop();
        }
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Record(QueryHistoryEntry entry)
    {
        var app = TickbirdApp.Current;
        if (app.Settings.KeepQueryHistory) app.History.Add(entry);
    }

    private static string FormatTimeout(int seconds) =>
        seconds % 3600 == 0 ? $"{seconds / 3600} h" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";

    /// <summary>Opens the find bar (Ctrl+F) or the find-and-replace bar (Ctrl+H), starting from the selected text.</summary>
    public void ShowFind(bool replace)
    {
        var selected = editor.SelectedText;
        if (!string.IsNullOrEmpty(selected) && !selected.Contains('\n')) searchPanel.SearchPattern = selected;
        searchPanel.IsReplaceMode = replace;
        searchPanel.Open();
        searchPanel.Reactivate();
    }

    /// <summary>The editor's find/replace bar (exposed for tests).</summary>
    public SearchPanel Search => searchPanel;

    public async Task OpenQueryAsync()
    {
        var path = await FilePicker.OpenAsync(this, "SQL files", "*.sql", "*.influxql", "*.txt");
        if (path == null) return;
        editor.Text = await File.ReadAllTextAsync(path);
        savedText = editor.Text;
    }

    /// <summary>Saves the query to a file. Returns false when nothing was saved (empty query or cancelled).</summary>
    public async Task<bool> SaveQueryAsync()
    {
        if (string.IsNullOrWhiteSpace(editor.Text))
        {
            if (OwnerWindow != null) await Dialogs.ShowMessageAsync(OwnerWindow, "Save Query", "Enter a query before saving.");
            return false;
        }
        var connection = Client?.Connection.Name ?? "InfluxDB";
        var path = await FilePicker.SaveAsync(this, ResultExporter.SafeFileName($"{connection}_{Database}_query.sql"), "SQL files", "*.sql");
        if (path == null) return false;
        await File.WriteAllTextAsync(path, editor.Text);
        savedText = editor.Text;
        return true;
    }

    public void CommentSelectedLines() => ReplaceSelectedOrCurrentLines(QueryText.Comment);

    public void UncommentSelectedLines() => ReplaceSelectedOrCurrentLines(QueryText.Uncomment);

    private void ReplaceSelectedOrCurrentLines(Func<string, string> transform)
    {
        if (editor.SelectionLength == 0)
        {
            var line = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
            editor.Select(line.Offset, line.Length);
        }
        editor.SelectedText = transform(editor.SelectedText);
        editor.Focus();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            dismissedAt = -1;
            ShowCompletion(explicitRequest: true);
        }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.F or Key.H)
        {
            e.Handled = true;
            completionWindow?.Close();
            ShowFind(replace: e.Key == Key.H);
        }
        else if (e.Key == Key.Escape && completionWindow != null)
        {
            // Esc keeps the list closed for the rest of this word; it comes back for the next one.
            e.Handled = true;
            dismissedAt = CurrentStart();
            completionWindow.Close();
        }
    }

    /// <summary>Start of the word or tag value being typed at the caret.</summary>
    private int CurrentStart()
    {
        if (CurrentTagValueContext() is { } value) return value.Context.Start;
        var caret = editor.TextArea.Caret.Offset;
        return CompletionTrigger.WordStart(editor.Document.GetText(0, caret), caret);
    }

    /// <summary>Where Esc closed the list (-1 when it was not dismissed).</summary>
    private int dismissedAt = -1;

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        // Fetch fields/tags of a FROM measurement that is not in the loaded schema yet; never blocks the list.
        _ = LoadContextSchemaQuietlyAsync();
        var typed = e.Text[^1];
        if (completionWindow != null)
        {
            // An open list filters itself while a word (or a tag value inside quotes) is typed. It is closed when the
            // word ends or nothing matches any more, so it can be offered again for the new position below.
            var stillTyping = showingTagValues ? CurrentTagValueContext() != null && typed != '\'' : CompletionTrigger.IsWordChar(typed);
            if (stillTyping && CompletionTexts.Count > 0) return;
            completionWindow.Close();
        }
        // After Esc, stay closed until the caret is in a different word or value.
        if (dismissedAt >= 0 && dismissedAt == CurrentStart()) return;

        // "host = ", "host != 'we" ...: offer the values of that tag (also re-offered if the list was closed meanwhile).
        if (CurrentTagValueContext() != null)
        {
            _ = ShowTagValuesAsync(explicitRequest: false);
            return;
        }
        var caret = editor.TextArea.Caret.Offset;
        var line = editor.Document.GetLineByOffset(caret);
        if (CompletionTrigger.ShouldOpen(editor.Document.GetText(0, caret), editor.Document.GetText(line.Offset, caret - line.Offset), e.Text))
            ShowCompletion(explicitRequest: false);
    }

    /// <summary>
    /// Completion list drawn inside the editor's window (overlay layer) instead of as a separate native popup
    /// window, so it keeps working while the user types. AvaloniaEdit's focus-loss check is replaced by the
    /// editor's own (see the LostFocus handler).
    /// </summary>
    private sealed class EditorCompletionWindow : CompletionWindow
    {
        public EditorCompletionWindow(TextArea textArea) : base(textArea) => ShouldUseOverlayLayer = true;

        protected override bool CloseOnFocusLost => false;
    }

    /// <summary>Closes the list as if something outside the editor had closed it (for tests).</summary>
    public void CloseCompletionExternally() => completionWindow?.Close();

    /// <summary>True when the open list is drawn inside the editor window (for tests).</summary>
    public bool CompletionUsesOverlay => completionWindow?.ShouldUseOverlayLayer == true;

    private async Task LoadContextSchemaQuietlyAsync()
    {
        try { await completion.LoadContextSchemaAsync(Client, Database, editor.Text); }
        catch { /* Some accounts cannot read schema metadata; the rest of the list remains available. */ }
    }

    private bool showingTagValues;

    /// <summary>The tag whose value is being typed at the caret, if the tag is known.</summary>
    private (TagValueContext Context, string Tag)? CurrentTagValueContext()
    {
        var caret = editor.TextArea.Caret.Offset;
        var line = editor.Document.GetLineByOffset(caret);
        var context = CompletionProvider.FindTagValueContext(editor.Document.GetText(0, caret), editor.Document.GetText(line.Offset, caret - line.Offset));
        var tag = context == null ? null : completion.FindTag(context.Tag);
        return context != null && tag != null ? (context, tag) : null;
    }

    /// <summary>Loads the tag's values if needed, then lists them (quoted) at the caret.</summary>
    public async Task ShowTagValuesAsync(bool explicitRequest)
    {
        if (CurrentTagValueContext() is not { } current) return;
        var measurement = CompletionProvider.FindMeasurement(editor.Text);
        var sources = completion.TagValueSources(measurement, current.Tag);
        var missing = sources.Where(m => !completion.HasTagValues(m, current.Tag)).ToList();
        if (missing.Count > 0 && TagValueLoader != null)
        {
            var previous = summary.Text;
            summary.Text = $"IntelliSense: loading values of tag {current.Tag}…";
            try
            {
                foreach (var m in missing) completion.SetTagValues(m, current.Tag, await TagValueLoader(m, current.Tag));
                summary.Text = previous;
            }
            catch (Exception ex)
            {
                summary.Text = $"IntelliSense: could not load values of tag {current.Tag} ({ex.Message})";
                return;
            }
            // The user may have moved on while the values were loading.
            if (CurrentTagValueContext() is not { } after || after.Tag != current.Tag) return;
            current = after;
        }

        var items = completion.GetTagValueItems(Client?.Connection.ServerVersion, sources, current.Tag).ToList();
        var typed = current.Context.Prefix;
        if (typed.Length > 0) items = items.Where(i => i.Text.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == 0) return;
        OpenWindow(items, current.Context.Start, explicitRequest, valueMode: true);
        var filter = editor.Document.GetText(current.Context.Start, editor.TextArea.Caret.Offset - current.Context.Start);
        if (filter.Length > 0) completionWindow?.CompletionList.SelectItem(filter);
    }

    /// <summary>Starts loading a tag's values in the background so they are ready when needed.</summary>
    private void PrefetchTagValues(string tag)
    {
        if (TagValueLoader == null) return;
        foreach (var m in completion.TagValueSources(CompletionProvider.FindMeasurement(editor.Text), tag))
            if (!completion.HasTagValues(m, tag))
                _ = PrefetchAsync(m);

        async Task PrefetchAsync(string measurement)
        {
            try { completion.SetTagValues(measurement, tag, await TagValueLoader(measurement, tag)); }
            catch { /* loaded again (and reported) when the values are actually requested */ }
        }
    }

    private void OpenWindow(IReadOnlyList<CompletionItem> items, int start, bool explicitRequest, bool valueMode)
    {
        completionWindow?.Close();
        var filterContext = !valueMode && CompletionProvider.IsFilterContext(editor.Document.GetText(0, start));
        var window = new EditorCompletionWindow(editor.TextArea)
        {
            StartOffset = start,
            CloseWhenCaretAtBeginning = !explicitRequest && !valueMode,
            MinWidth = 320
        };
        foreach (var item in items) window.CompletionList.CompletionData.Add(new CompletionEntry(item, filterContext, OnCompleted));
        completionWindow = window;
        showingTagValues = valueMode;
        window.Closed += (_, _) =>
        {
            if (completionWindow != window) return;
            completionWindow = null;
            showingTagValues = false;
        };
        window.Show();
        window.CompletionList.SelectedItem = window.CompletionList.CompletionData.FirstOrDefault();
    }

    private void OnCompleted(CompletionItem item, bool insertedComparison)
    {
        if (item.Kind != CompletionKind.Tag) return;
        // A tag picked in a WHERE condition became "tag = ": show its values next. Elsewhere, just get them ready.
        if (insertedComparison) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowTagValuesAsync(explicitRequest: true));
        else PrefetchTagValues(item.Text);
    }

    /// <summary>Inserts the selected list entry, as Enter/Tab would (for tests).</summary>
    public void CommitSelectedCompletion() => completionWindow?.CompletionList.RequestInsertion(EventArgs.Empty);

    /// <summary>True while the list shows tag values (for tests).</summary>
    public bool IsShowingTagValues => completionWindow != null && showingTagValues;

    /// <summary>Opens the list for the word at the caret, filtered by what has been typed so far.</summary>
    public void ShowCompletion(bool explicitRequest)
    {
        if (CurrentTagValueContext() != null)
        {
            _ = ShowTagValuesAsync(explicitRequest);
            return;
        }
        completionWindow?.Close();
        var caret = editor.TextArea.Caret.Offset;
        var text = editor.Document.GetText(0, caret);
        var start = CompletionTrigger.WordStart(text, caret);
        var prefix = text[start..].TrimStart('"');
        var items = completion.GetItems(Client?.Connection.ServerVersion, editor.Text, text[..start]).ToList();
        if (prefix.Length > 0)
        {
            // Open only when something matches what was typed.
            var matches = items.Where(i => i.Text.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && !explicitRequest) return;
            if (matches.Count > 0) items = matches;
        }
        if (items.Count == 0) return;

        OpenWindow(items, start, explicitRequest, valueMode: false);
        if (prefix.Length > 0) completionWindow?.CompletionList.SelectItem(prefix);
    }

    /// <summary>True while the completion list is open (for tests).</summary>
    public bool IsCompletionOpen => completionWindow != null;

    /// <summary>The entries currently shown in the list, after filtering by the typed text (for tests).</summary>
    public IReadOnlyList<string> CompletionTexts =>
        completionWindow?.CompletionList.ListBox?.Items.OfType<ICompletionData>().Select(d => d.Text).ToList()
        ?? completionWindow?.CompletionList.CompletionData.Select(d => d.Text).ToList()
        ?? new List<string>();

    private sealed class CompletionEntry : ICompletionData
    {
        private readonly CompletionItem item;
        private readonly bool filterContext;
        private readonly Action<CompletionItem, bool> completed;

        public CompletionEntry(CompletionItem item, bool filterContext, Action<CompletionItem, bool> completed)
        {
            this.item = item;
            this.filterContext = filterContext;
            this.completed = completed;
        }
        public string Text => item.Text;
        public object Content => item.Text;
        public object Description => item.Description;
        public IImage? Image => item.Kind switch
        {
            CompletionKind.Measurement => ShellIcons.Get("Measurement"),
            CompletionKind.Field => ShellIcons.Get("FieldKeys"),
            CompletionKind.Tag => ShellIcons.Get("TagKeys"),
            CompletionKind.TagValue => ShellIcons.Get("TagValues"),
            _ => null
        };
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // In a WHERE condition a tag becomes "tag = " so its values can be offered straight away.
            var comparison = item.Kind == CompletionKind.Tag && filterContext && !NextNonSpaceIsOperator(textArea, completionSegment.EndOffset);
            textArea.Document.Replace(completionSegment, comparison ? item.InsertText + " = " : item.InsertText);
            // Put the caret between the brackets of a function, e.g. mean(|).
            if (item.InsertText.EndsWith("()", StringComparison.Ordinal)) textArea.Caret.Offset -= 1;
            completed(item, comparison);
        }

        private static bool NextNonSpaceIsOperator(TextArea textArea, int offset)
        {
            var document = textArea.Document;
            while (offset < document.TextLength && document.GetCharAt(offset) == ' ') offset++;
            return offset < document.TextLength && document.GetCharAt(offset) is '=' or '!' or '<' or '>';
        }
    }

    /// <summary>Highlights InfluxQL keywords, functions, strings and comments.</summary>
    /// <summary>
    /// Syntax colors as in SQL Server Management Studio: keywords blue, functions magenta, strings red,
    /// comments green, operators (including AND, OR, NOT, IN...) gray; names and numbers stay black.
    /// </summary>
    private sealed class InfluxQlColorizer : DocumentColorizingTransformer
    {
        private static readonly Regex Token = new(
            @"--.*$|'(?:[^']|'')*'?|""(?:[^""])*""?|\b[A-Za-z_][A-Za-z0-9_]*\b|=~|!~|<>|!=|<=|>=|[=<>!*+\-/%,()]",
            RegexOptions.Compiled);

        // Logical operators that SSMS shows in gray rather than blue.
        private static readonly HashSet<string> OperatorWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "OR", "NOT", "IN", "IS", "NULL", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "ALL", "ANY", "SOME"
        };
        private static readonly HashSet<string> Keywords = new(
            // Multi-word entries ("GROUP BY", "SHOW TAG KEYS") color each of their words.
            CompletionProvider.InfluxQlKeywords.Where(k => k.Description == "Keyword").SelectMany(k => k.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Concat(new[] { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "USING", "UNION", "HAVING", "CASE", "WHEN", "THEN", "ELSE", "INTERVAL", "TABLES", "COLUMNS" }),
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Functions = new(
            CompletionProvider.InfluxQlKeywords.Where(k => k.Description == "Function").Select(k => k.Text.TrimEnd('(', ')'))
                .Concat(new[] { "AVG", "DATE_BIN", "DATE_BIN_GAPFILL", "DATE_TRUNC", "DATE_PART", "EXTRACT", "CAST", "COALESCE", "NULLIF",
                                "SELECTOR_FIRST", "SELECTOR_LAST", "SELECTOR_MIN", "SELECTOR_MAX", "LOCF", "INTERPOLATE", "TO_TIMESTAMP",
                                "APPROX_PERCENTILE_CONT", "FIRST_VALUE", "LAST_VALUE", "CONCAT", "LOWER", "UPPER", "LENGTH", "SUBSTR", "REPLACE", "TRIM", "POWER" }),
            StringComparer.OrdinalIgnoreCase);

        public static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF));     // blue
        public static readonly IBrush FunctionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF));    // magenta
        public static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));      // red
        public static readonly IBrush CommentBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00));     // green
        public static readonly IBrush OperatorBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));    // gray

        /// <summary>The color a token gets, or null for the default (black). Exposed for tests.</summary>
        public static IBrush? BrushFor(string token)
        {
            if (token.StartsWith("--", StringComparison.Ordinal)) return CommentBrush;
            if (token[0] == '\'') return StringBrush;
            if (token[0] == '"') return null;                                   // quoted identifier: a name
            if (!char.IsLetter(token[0]) && token[0] != '_') return OperatorBrush;
            if (OperatorWords.Contains(token)) return OperatorBrush;
            if (Keywords.Contains(token)) return KeywordBrush;
            return Functions.Contains(token) ? FunctionBrush : null;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            foreach (Match m in Token.Matches(text))
            {
                var brush = BrushFor(m.Value);
                if (brush == null) continue;
                ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length,
                    element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    /// <summary>The syntax color of a token in the editor, or null for plain black (for tests).</summary>
    public static IBrush? SyntaxBrush(string token) => InfluxQlColorizer.BrushFor(token);
}
