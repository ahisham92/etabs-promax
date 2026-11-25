using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CsiProMax.UI;

/// <summary>
/// The pane's Review tab: makes the review pack of the open model (the same as the chat's export_pack tool) into the
/// model's own folder, beside the model file, and lists the packs already there. Laid out as an ETABS form: group boxes
/// with dark blue captions and the buttons centred at the foot. It only reads the model.
/// </summary>
internal sealed class ReviewView : ScrollViewer
{
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new() { IsReadOnly = true };
    private readonly TextBox _drift = new() { Width = 70 };
    private readonly TextBox _design = new() { Width = 70, Text = "1.0" };
    private readonly ListView _packs = new() { MinHeight = 90, MaxHeight = 220 };
    private readonly TextBlock _status = Brand.Text("", Brand.K.Muted, 11);
    private readonly Button _export = Brand.Button("Export Pack", Brand.Weight.Primary);
    private string _shownFolder = "";

    public ReviewView()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetResourceReference(BackgroundProperty, Brand.K.Bg);
        AutomationProperties.SetName(this, "Review pack");
        var stack = new StackPanel { Margin = new Thickness(8, 10, 8, 8) };

        var where = new Grid();
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        where.ColumnDefinitions.Add(new ColumnDefinition());
        where.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        where.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        where.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(where, 0, "File _name", _name);
        Put(where, 1, "Saved in", _folder);
        var note = Brand.Text("Saved in the model's own folder, next to the model file.", Brand.K.Muted, 11);
        note.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetRow(note, 2);
        Grid.SetColumnSpan(note, 2);
        where.Children.Add(note);
        stack.Children.Add(Brand.Group("Review Pack", where, Brand.K.Bg));

        var limits = new Grid();
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        limits.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        limits.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Put(limits, 0, "_Drift limit", _drift);
        Put(limits, 1, "D_esign ratio limit", _design);
        _drift.ToolTip = "Allowed drift ratio for the check report in the pack, for example 0.02. Leave empty for values only.";
        stack.Children.Add(Brand.Group("Check Limits", limits, Brand.K.Bg));

        var open = Brand.Button("Open Folder");
        _export.Click += async (_, _) => await Export();
        open.Click += (_, _) => OpenFolder();
        stack.Children.Add(Brand.ButtonRow(_export, open));
        _status.Margin = new Thickness(0, 6, 0, 6);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_status);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "File", DisplayMemberBinding = new System.Windows.Data.Binding("File"), Width = 170 });
        view.Columns.Add(new GridViewColumn { Header = "Saved", DisplayMemberBinding = new System.Windows.Data.Binding("Saved"), Width = 96 });
        view.Columns.Add(new GridViewColumn { Header = "Size", DisplayMemberBinding = new System.Windows.Data.Binding("Size"), Width = 60 });
        _packs.View = view;
        AutomationProperties.SetName(_packs, "Earlier packs");
        _packs.MouseDoubleClick += (_, _) => { if (_packs.SelectedItem is PackRow row) Shell(row.Path); };
        stack.Children.Add(Brand.Group("Earlier Packs", _packs, Brand.K.Bg));

        Content = stack;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Refresh(); };
    }

    private sealed record PackRow(string File, string Saved, string Size, string Path);

    private static void Put(Grid grid, int row, string label, Control field)
    {
        var l = new Label { Content = label, Target = field, Padding = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        field.Margin = new Thickness(0, 2, 0, 2);
        AutomationProperties.SetName(field, label.Replace("_", ""));
        Grid.SetRow(l, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(l);
        grid.Children.Add(field);
    }

    /// <summary>Reads where the pack goes and lists the packs already there.</summary>
    public async void Refresh()
    {
        if (App.Etabs is not { } etabs) { _status.Text = "Open a model first."; return; }
        string file;
        try { file = await Task.Run(() => etabs.Link.Info().File); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "The model's file could not be read: " + ex.Message; return; }
        var (folder, why) = CsiProMax.Tools.ExportPackTool.BesideModel(file, CsiProMax.Tools.MakeDocumentTool.DefaultFolder);
        _shownFolder = folder;
        _folder.Text = folder;
        if (_name.Text.Length == 0 && file.Length > 0) _name.Text = Path.GetFileNameWithoutExtension(file);
        if (why is not null) _status.Text = "The pack goes to " + folder + ": " + why + ".";
        var rows = new List<PackRow>();
        try
        {
            if (Directory.Exists(folder))
                foreach (var f in new DirectoryInfo(folder).GetFiles("*.cpmx").OrderByDescending(f => f.LastWriteTime).Take(30))
                    rows.Add(new PackRow(f.Name, f.LastWriteTime.ToString("d MMM HH:mm"), Math.Max(1, f.Length / 1024) + " KB", f.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _packs.ItemsSource = rows;
    }

    private async Task Export()
    {
        if (App.Etabs is not { } etabs) { _status.Text = "Open a model first."; return; }
        var raw = new Dictionary<string, JsonElement>();
        if (_name.Text.Trim() is { Length: > 0 } name) raw["name"] = JsonSerializer.SerializeToElement(name);
        if (!Limit(_drift, "drift_limit", raw) || !Limit(_design, "design_limit", raw)) return;
        Brand.SetBusy(_export, true, "Exporting…");
        _status.Text = "Reading the model and its result tables…";
        try
        {
            var args = new CsiProMax.Tools.ToolArgs(raw);
            var result = await Task.Run(() => CsiProMax.Tools.ExportPackTool.Make(etabs.Link, args, CsiProMax.Tools.MakeDocumentTool.DefaultFolder));
            if (result.IsError) { _status.Text = result.Text; return; }
            using var doc = JsonDocument.Parse(result.Text);
            var pack = doc.RootElement.TryGetProperty("pack", out var p) ? p.GetString() : null;
            var viewer = doc.RootElement.TryGetProperty("viewer", out var v) ? v.GetString() : null;
            _status.Text = $"Saved {Path.GetFileName(pack)} and {Path.GetFileName(viewer)}. Send the viewer page: it opens in any browser.";
            Refresh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "The pack could not be made: " + ex.Message;
        }
        finally { Brand.SetBusy(_export, false); }
    }

    private bool Limit(TextBox box, string key, Dictionary<string, JsonElement> raw)
    {
        var text = box.Text.Trim();
        if (text.Length == 0) return true;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            raw[key] = JsonSerializer.SerializeToElement(value);
            return true;
        }
        _status.Text = "Type the limit as a number, for example 0.02.";
        box.Focus();
        return false;
    }

    private void OpenFolder()
    {
        if (_shownFolder.Length == 0 || !Directory.Exists(_shownFolder)) { _status.Text = "The folder isn't there yet: export a pack first."; return; }
        Shell(_shownFolder);
    }

    private static void Shell(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
