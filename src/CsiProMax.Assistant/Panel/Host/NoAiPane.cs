using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CsiProMax.Tools;
using CsiProMax.UI;

namespace CsiProMax.Host;

/// <summary>
/// The window's content when the AI chat is off on this PC (AiMode): in place of the chat, what CSI ProMax does by
/// hand. In the Modeler: the 3D view to model in (Draw, Assign, Define, Edit, the Excel tables), opened by itself at
/// the first start. Beside a CSI program: the open model in 3D, a review pack for someone without the program, and
/// the Modeler. The Viewer for a pack someone sent opens from here too. Our own window, so the Sketch ProMax look.
/// </summary>
internal static class NoAiPane
{
    /// <summary>The texts of the pane's buttons, for the tests and the field test (the same names the window shows).</summary>
    internal static IReadOnlyList<string> Actions(bool modeler, string program) => modeler
        ? ["Open the 3D view and model by hand", "Open the models folder", "Open the Viewer (a pack someone sent)", "Turn the AI chat on"]
        : ["Open the 3D view of this model", "Make a review pack of this model", $"Open the Modeler (no {program} needed)", "Open the Viewer (a pack someone sent)", "Turn the AI chat on"];

    public static FrameworkElement Build(bool reopened)
    {
        var modeler = CsiProMax.App.Modeler;
        var names = Actions(modeler, Csi.Name);
        var root = new DockPanel { LastChildFill = true };
        root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, Brand.K.Bg);
        var header = Brand.Header(CsiProMax.App.Product, "Without the AI chat");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
        body.Children.Add(Brand.Text(modeler
            ? "Build the model by hand in the 3D view, which opens in your browser: Draw, Assign, Define and Edit, and File > Import/Export for the Excel tables. " +
              $"Save it as a model file that {Csi.Name} opens. Nothing here needs a CSI program, a licence of one, or the internet."
            : $"Beside {Csi.Name}: see the open model in 3D in your browser, make a review pack for someone who has no {Csi.Name}, or build a model by hand in the Modeler.",
            Brand.K.Text, 13));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        Button Add(string text, Action run, bool primary = false)
        {
            var button = Brand.Button(text, primary ? Brand.Weight.Primary : Brand.Weight.Secondary);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(0, 0, 0, 8);
            button.Padding = new Thickness(12, 7, 12, 7);
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            button.Click += (_, _) =>
            {
                try { run(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("CSI ProMax", $"That didn't work: {ex.Message}"); }
            };
            list.Children.Add(button);
            return button;
        }
        var i = 0;
        Add(names[i++], ModelView.Open, primary: true);
        if (modeler)
            Add(names[i++], OpenModelsFolder);
        else
        {
            Button? pack = null;
            pack = Add(names[i++], () => MakePack(pack!));
            Add(names[i++], ModelView.StartModeler);
        }
        Add(names[i++], OpenViewer);
        body.Children.Add(list);

        var off = new Border { Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        off.SetResourceReference(Border.BackgroundProperty, Brand.K.Surface);
        off.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);
        var offText = new StackPanel();
        offText.Children.Add(Brand.Text("The AI chat is off on this PC", Brand.K.Text, 12.5, bold: true));
        offText.Children.Add(Brand.Text("No Claude Code is used or installed. Turn the chat on at any time: CSI ProMax starts again with it, and " +
                                        $"{Csi.Name} and your model stay open.", Brand.K.Muted));
        var on = Brand.Button(names[i], Brand.Weight.Secondary);
        on.HorizontalAlignment = HorizontalAlignment.Left;
        on.Margin = new Thickness(0, 8, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(on, names[i]);
        on.Click += (_, _) => MainWindow.SwitchAi(true);
        offText.Children.Add(on);
        off.Child = offText;
        body.Children.Add(off);

        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        // The Modeler's work is in the 3D view: it opens by itself at the first start (not again after an update or a switch).
        if (modeler && !reopened)
        {
            var once = false;
            root.Loaded += (_, _) =>
            {
                if (once) return;
                once = true;
                try { ModelView.Open(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* the button opens it */ }
            };
        }
        return root;
    }

    private static void OpenModelsFolder()
    {
        Directory.CreateDirectory(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder);
        Process.Start(new ProcessStartInfo(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder) { UseShellExecute = true })?.Dispose();
    }

    private static void OpenViewer() =>
        Process.Start(new ProcessStartInfo(Shortcuts.WriteViewerPage()) { UseShellExecute = true })?.Dispose();

    /// <summary>The review pack of the open model, beside the model file, the same as the chat's export_pack; then the Viewer page opens.</summary>
    private static async void MakePack(Button button)
    {
        if (CsiProMax.App.Etabs is null || Brand.IsBusy(button)) return;
        Brand.SetBusy(button, true, "Making the review pack…");
        ToolResult result;
        try
        {
            var args = new ToolArgs(new Dictionary<string, JsonElement>());
            result = await CsiProMax.App.Dispatcher.InvokeAsync(app => ExportPackTool.Make(app.Link, args, MakeDocumentTool.DefaultFolder));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { result = ToolResult.Error("The pack could not be made: " + ex.Message); }
        finally { Brand.SetBusy(button, false); }
        if (result.IsError) { TaskDialog.Show("CSI ProMax", result.Text); return; }
        using var made = JsonDocument.Parse(result.Text);
        var page = made.RootElement.TryGetProperty("viewer", out var v) ? v.GetString() ?? "" : "";
        var folder = made.RootElement.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
        var elsewhere = made.RootElement.TryGetProperty("not_beside_model", out var n) && n.ValueKind == JsonValueKind.String ? "\n\nIt is not beside the model: " + n.GetString() : "";
        TaskDialog.Show("CSI ProMax", $"The review pack is in {folder}.{elsewhere}\n\nSend the Viewer page ({Path.GetFileName(page)}): it opens in any browser with the pack inside, with nothing to install.");
        try
        {
            if (page.Length > 0) Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
