using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CsiProMax.UI;

/// <summary>
/// The pane's Checks tab, laid out like ETABS's Model Explorer: a toolbar, the checks as a tree with a pass, fail or
/// warning mark on each, and the selected check's values in a two-column property grid below. It runs the same check
/// report the chat's check_report tool runs (every value read from the program's own result tables), off the UI thread.
/// Nothing in the model is changed.
/// </summary>
internal sealed class ChecksView : DockPanel
{
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Padding = new Thickness(2, 4, 2, 4) };
    private readonly Grid _details = new();
    private readonly TextBlock _detailsTitle = Brand.Text("", Brand.K.Text);
    private readonly TextBlock _status = Brand.Text("Press Run Checks to check the open model's results.", Brand.K.Muted, 11);
    private readonly TextBox _drift = new() { Width = 64, ToolTip = "Allowed drift ratio, for example 0.02. Leave empty to report drift without a verdict." };
    private readonly Button _run = Brand.Button("Run Checks");
    private bool _running;

    public ChecksView()
    {
        LastChildFill = true;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        AutomationProperties.SetName(this, "Checks");

        // Toolbar: Run Checks and the drift limit, on the pane's light strip.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 3, 4, 3) };
        _run.Margin = new Thickness(0, 0, 8, 0);
        _run.Click += async (_, _) => await Run();
        bar.Children.Add(_run);
        var driftLabel = new Label { Content = "_Drift limit", Target = _drift, Padding = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(driftLabel);
        _drift.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(_drift, "Drift limit");
        bar.Children.Add(_drift);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        barBorder.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        SetDock(barBorder, Dock.Top);
        Children.Add(barBorder);

        // Foot: the count of checks by verdict.
        var foot = new Border { Child = _status, Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 1, 0, 0) };
        foot.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.BorderSubtle);
        SetDock(foot, Dock.Bottom);
        Children.Add(foot);

        // The property grid of the selected check.
        var head = new Border { Child = _detailsTitle, Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 1, 0, 1) };
        head.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        head.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.44, GridUnitType.Star) });
        _details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.56, GridUnitType.Star) });
        var detailStack = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        detailStack.Children.Add(head);
        detailStack.Children.Add(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260 });
        SetDock(detailStack, Dock.Bottom);
        Children.Add(detailStack);

        AutomationProperties.SetName(_tree, "Checks and their results");
        _tree.SelectedItemChanged += (_, e) => ShowDetails((e.NewValue as TreeViewItem)?.Tag as CheckRow);
        Children.Add(_tree);
        ShowDetails(null);
    }

    private sealed record CheckRow(string Name, string Status, JsonObject? Values);

    private async Task Run()
    {
        if (_running) return;
        if (App.Etabs is not { } etabs) { _status.Text = "Open a model first: CSI ProMax isn't connected to a program yet."; return; }
        var args = new JsonObject();
        var limitText = _drift.Text.Trim();
        if (limitText.Length > 0)
        {
            if (!double.TryParse(limitText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var limit) || limit <= 0 || limit >= 1)
            {
                _status.Text = "The drift limit is a ratio between 0 and 1, for example 0.02.";
                _drift.Focus();
                return;
            }
            args["drift_limit"] = limit;
        }
        _running = true;
        Brand.SetBusy(_run, true, "Running…");
        _status.Text = "Reading the result tables…";
        try
        {
            var element = JsonDocument.Parse(args.ToJsonString()).RootElement.Clone();
            var report = await Task.Run(() => CsiProMax.Assistant.Tools.ProgramTools.Report(element, etabs.Link));
            Show(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "The checks could not run: " + ex.Message;
        }
        finally
        {
            Brand.SetBusy(_run, false);
            _running = false;
        }
    }

    /// <summary>Fills the tree from a check report: {"model", "checks": {name: {"status", ...}}}.</summary>
    private void Show(JsonNode? report)
    {
        _tree.Items.Clear();
        var rows = Rows(report);
        var model = report?["model"]?.GetValue<string>() is { Length: > 0 } file ? System.IO.Path.GetFileName(file) : "the open model";
        var root = new TreeViewItem { Header = Row(Folder(), "Checks for " + model), IsExpanded = true };
        foreach (var row in rows)
        {
            var item = new TreeViewItem { Header = Row(Mark(row.Status), $"{Title(row.Name)} ({row.Status})"), Tag = row };
            AutomationProperties.SetName(item, $"{Title(row.Name)}: {row.Status}");
            root.Items.Add(item);
        }
        _tree.Items.Add(root);
        int pass = rows.Count(r => r.Status == "pass"), fail = rows.Count(r => r.Status == "fail"), other = rows.Count - pass - fail;
        _status.Text = rows.Count == 0
            ? "The report had no checks in it."
            : $"{rows.Count} checks: {pass} OK, {fail} not OK, {other} reported or not available.";
        if (root.Items.Count > 0 && root.Items[0] is TreeViewItem first)
            first.IsSelected = true;
    }

    internal static List<(string Name, string Status)> Verdicts(JsonNode? report) => Rows(report).Select(r => (r.Name, r.Status)).ToList();

    private static List<CheckRow> Rows(JsonNode? report)
    {
        var rows = new List<CheckRow>();
        if (report?["checks"] is not JsonObject checks) return rows;
        foreach (var (name, node) in checks)
        {
            var obj = node as JsonObject;
            var status = obj?["status"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "reported";
            rows.Add(new CheckRow(name, status, obj));
        }
        return rows;
    }

    private static string Title(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private void ShowDetails(CheckRow? row)
    {
        _details.Children.Clear();
        _details.RowDefinitions.Clear();
        _detailsTitle.Text = row is null ? "Select a check to see its values" : Title(row.Name);
        if (row?.Values is not { } values) return;
        var r = 0;
        foreach (var (key, node) in values)
        {
            _details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = Cell(Title(key), Brand.K.Surface);
            var v = Cell(Describe(node), Brand.K.Bg);
            if (key == "status" && row.Status == "fail") Brand.Fg((TextBlock)v.Child, Brand.K.DangerText);
            Grid.SetRow(k, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            _details.Children.Add(k);
            _details.Children.Add(v);
            r++;
        }
    }

    /// <summary>A value as one line: text and numbers as they are, a list by its count and first items, an object by its keys.</summary>
    internal static string Describe(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value => value.ToJsonString().Trim('"'),
        JsonArray list when list.Count == 0 => "none",
        JsonArray list when list.All(i => i is JsonValue) => string.Join(", ", list.Take(6).Select(i => i!.ToJsonString().Trim('"'))) + (list.Count > 6 ? $" … ({list.Count})" : ""),
        JsonArray list => $"{list.Count} rows",
        JsonObject obj => string.Join(", ", obj.Take(4).Select(p => $"{p.Key} {Describe(p.Value)}")) + (obj.Count > 4 ? " …" : ""),
        _ => node.ToJsonString(),
    };

    private static Border Cell(string text, string background)
    {
        var t = Brand.Text(text, Brand.K.Text, 11);
        var cell = new Border { Child = t, Padding = new Thickness(5, 2, 5, 2), BorderThickness = new Thickness(0, 0, 1, 1) };
        cell.SetResourceReference(Border.BackgroundProperty, background);
        cell.SetResourceReference(Border.BorderBrushProperty, Brand.K.GridLine);
        return cell;
    }

    private static StackPanel Row(UIElement icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // ---- Our own 14 px marks: green tick, red cross, amber warning, grey dash, yellow folder ----

    internal static FrameworkElement Mark(string status) => status switch
    {
        "pass" => Glyph("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z", 0x2E9B45, 0x1D6B2E, "M5,8.2 L7,10.2 L11,6.2", "OK"),
        "fail" => Glyph("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z", 0xC8321F, 0x8E2114, "M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5", "Not OK"),
        "warning" => Glyph("M8,1.5 L15,14.5 L1,14.5 Z", 0xF2C12E, 0x9A7408, "M8,6 L8,10 M8,11.6 L8,12.4", "Warning", markColour: 0x1A1A1A),
        _ => Glyph("M8,1.5 A6.5,6.5 0 1 1 7.99,1.5 Z", 0xB9B9B9, 0x7A7A7A, "M5,8 L11,8", status),
    };

    private static FrameworkElement Folder() => Glyph("M1.5,4 L6.5,4 L8,5.5 L14.5,5.5 L14.5,13.5 L1.5,13.5 Z", 0xF2C45A, 0xA57A12, "", "Checks");

    private static FrameworkElement Glyph(string shape, uint fill, uint stroke, string mark, string name, uint markColour = 0xFFFFFF)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(shape), Fill = Brand.Brush(C(fill)), Stroke = Brand.Brush(C(stroke)), StrokeThickness = 1 });
        if (mark.Length > 0)
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(mark), Stroke = Brand.Brush(C(markColour)), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var box = new Viewbox { Width = 14, Height = 14, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static Color C(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
