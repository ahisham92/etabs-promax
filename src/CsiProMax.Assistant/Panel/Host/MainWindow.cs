using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CsiProMax.UI;

namespace CsiProMax.Host;

/// <summary>
/// The CSI ProMax window: Sketch ProMax's chat pane in a window of its own beside ETABS (ETABS has no dockable panes
/// for plug-ins). It can be resized and maximised, remembers where it was, and opens narrow on the right of the screen
/// the first time, like the pane in Revit. Closing it ends CSI ProMax; ETABS and the model are not touched.
/// </summary>
internal sealed class MainWindow : Window
{
    private static MainWindow? _open;

    /// <summary>This window's handle (zero before it is shown): the owner of CSI ProMax's dialogs and tool windows.</summary>
    public static IntPtr Handle { get; private set; }

    public static MainWindow? Current => _open;

    private static string PlaceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "window.txt");

    public MainWindow()
    {
        _open = this;
        Title = CsiProMax.App.Product;
        MinWidth = 340;
        MinHeight = 420;
        if (Brand.Icon is { } icon) Icon = icon;
        Brand.UseTheme(this);
        Place();
        var layout = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        layout.Children.Add(menu);
        layout.Children.Add(Chat.ChatPane.Get());
        Content = layout;
        SourceInitialized += (_, _) => Handle = new WindowInteropHelper(this).Handle;
        Closing += (_, _) => Remember();
        Closed += (_, _) => { Handle = IntPtr.Zero; _open = null; };
    }

    /// <summary>
    /// What Sketch ProMax has on Revit's ribbon and ETABS has no place for: the license, settings, notes, routines,
    /// folders, bug reports, the self test and updates, in one menu above the chat.
    /// </summary>
    private Menu BuildMenu()
    {
        var menu = new Menu { Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(4, 2, 4, 2) };
        System.Windows.Automation.AutomationProperties.SetName(menu, "CSI ProMax menu");
        MenuItem Top(string header)
        {
            var item = new MenuItem { Header = header };
            item.SetResourceReference(ForegroundProperty, "SpmText");
            System.Windows.Automation.AutomationProperties.SetName(item, header.Replace("_", ""));
            menu.Items.Add(item);
            return item;
        }
        void Add(MenuItem parent, string header, Action run)
        {
            var item = new MenuItem { Header = header, Foreground = SystemColors.MenuTextBrush };
            System.Windows.Automation.AutomationProperties.SetName(item, header.Replace("_", "").TrimEnd('…'));
            item.Click += (_, _) =>
            {
                try { run(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    TaskDialog.Show("CSI ProMax", $"That didn't work: {ex.Message}");
                }
            };
            parent.Items.Add(item);
        }
        void Dialog(Window window) { Brand.Prepare(window); window.ShowDialog(); }

        var app = Top("_CSI ProMax");
        Add(app, "_License…", () => LicenseWindow.Open());
        if (Licensing.LicenseState.IsOwnerPc) Add(app, "License _manager…", () => LicenseManagerWindow.Open());
        Add(app, "_Settings…", () => Dialog(new SettingsWindow()));
        Add(app, "Check for _updates", CsiProMax.App.CheckForUpdatesInteractive);
        Add(app, "_About", () => TaskDialog.Show("CSI ProMax",
            $"CSI ProMax {Update.Updater.CurrentText}\nETABS {(CsiProMax.App.ETABSYear.Length > 0 ? CsiProMax.App.ETABSYear : "not answering yet")}\n" +
            $"Model: {ModelWatch.Current?.Title ?? "none"}\n\n{Update.Updater.Describe()}"));
        app.Items.Add(new Separator());
        Add(app, "_Close CSI ProMax", Close);

        var chat = Top("_Chat");
        Add(chat, "_Notes…", OpenNotes);
        Add(chat, "_Routines…", Chat.ChatPane.OpenRoutinesWindow);
        Add(chat, "_Team performance…", () => { var w = new CrewPerformanceWindow(); Brand.Prepare(w); w.Show(); });

        var view = Top("_View");
        Add(view, "Open the _3D view", ModelView.Open);
        if (CsiProMax.App.Modeler)
            Add(view, "Open the _models folder", () =>
            {
                Directory.CreateDirectory(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder) { UseShellExecute = true })?.Dispose();
            });
        else
        {
            view.Items.Add(new Separator());
            Add(view, "Open the _Modeler (no ETABS needed)", ModelView.StartModeler);
        }
        Add(view, "Make the Modeler and Viewer _shortcuts again", () => TaskDialog.Show("CSI ProMax", "The Modeler and Viewer shortcuts are on your desktop and in the Start menu (CSI ProMax):\n" + string.Join("\n", Shortcuts.MakeAll())));

        var help = Top("_Help");
        Add(help, "_Report a bug…", () => { if (CsiProMax.App.Etabs is { } etabs) Bugs.BugReport.Command(etabs); });
        Add(help, "Open the _Bugs folder", () => Bugs.BugReport.OpenFolder());
        Add(help, "_Export chats and bugs", ExportChatsAndBugs);
        Add(help, "Open _folders…", () => UserFoldersWindow.Open());
        Add(help, "Self _test", RunSelfTest);
        help.Items.Add(new Separator());
        Add(help, "_Field test (saves a report to send)", () => RunFieldTest(false));
        Add(help, "Field test with _changes (scratch model only)…", () => RunFieldTest(true));
        return menu;
    }

    private static Window? _notes;

    private void OpenNotes()
    {
        if (_notes is { } open) { open.Activate(); return; }
        var view = Chat.NotesView.Get();
        if (view.Parent is Window old) old.Content = null;
        var window = new Window { Title = "CSI ProMax: Notes", Width = 520, Height = 640, Content = view, Owner = this };
        if (Brand.Icon is { } icon) window.Icon = icon;
        Brand.UseTheme(window);
        window.Closed += (_, _) => { window.Content = null; _notes = null; };
        _notes = window;
        window.Show();
    }

    private static void ExportChatsAndBugs()
    {
        if (CsiProMax.App.Etabs is not { } etabs) return;
        var result = System.Text.Json.JsonSerializer.Serialize(Bugs.ChatsAndBugsExport.Export(etabs, "week"));
        var file = Chat.ChatFiles.Find(result).FirstOrDefault();
        TaskDialog.Show("CSI ProMax", file is null
            ? "The export of this week's chats and bugs was made in " + Bugs.ChatsAndBugsExport.Folder + "."
            : "This week's chats and bugs were exported to:\n" + file);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Bugs.ChatsAndBugsExport.Folder}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void RunSelfTest()
    {
        if (CsiProMax.App.Etabs is not { } etabs) return;
        if (CsiProMax.Assistant.SelfTest.IsRunning) { TaskDialog.Show("CSI ProMax", "A self test is already running."); return; }
        List<CsiProMax.Assistant.SelfTest.StepResult> steps;
        try { steps = await Task.Run(() => CsiProMax.Assistant.SelfTest.Steps(etabs.Link, changes: false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("CSI ProMax", "The self test could not run: " + ex.Message); return; }
        var bad = steps.Where(s => !s.Ok).ToList();
        TaskDialog.Show("CSI ProMax", bad.Count == 0
            ? $"Self test: all {steps.Count} checks passed. CSI ProMax can read the open model. Nothing was changed."
            : $"Self test: {steps.Count - bad.Count} of {steps.Count} checks passed. Not working:\n" + string.Join("\n", bad.Select(s => $"- {s.Name}: {s.Detail}")));
    }

    /// <summary>
    /// The field test: the self test's steps and the result checks on the open model, saved as one report file that the
    /// person sends back. With changes it also adds and deletes test objects, which is asked first: no undo in ETABS.
    /// </summary>
    private async void RunFieldTest(bool changes)
    {
        if (CsiProMax.App.Etabs is not { } etabs) return;
        if (CsiProMax.Assistant.SelfTest.IsRunning) { TaskDialog.Show("CSI ProMax", "A test is already running."); return; }
        if (changes && TaskDialog.Show("CSI ProMax",
                "This adds a test column, a slab, the load pattern CPM-TEST, loads and the combination CPM-TEST-COMBO to the model that is open now, " +
                "then deletes the column and the slab again. The load pattern and the combination stay, and there is no undo.\n\n" +
                "Use it only on a scratch model, never on a project model. Go on?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No) != TaskDialogResult.Yes) return;
        CsiProMax.Assistant.FieldTest.Outcome outcome;
        try { outcome = await Task.Run(() => CsiProMax.Assistant.FieldTest.Run(etabs.Link, changes, "the Help menu")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("CSI ProMax", "The field test could not run: " + ex.Message); return; }
        TaskDialog.Show("CSI ProMax", outcome.Summary());
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{outcome.File}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Brings the window forward (ETABS's menu was used again while it is open).</summary>
    public void Surface()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        try
        {
            if (File.Exists(PlaceFile))
            {
                var f = File.ReadAllText(PlaceFile).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 5 && double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l)
                    && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)
                    && double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
                    && double.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
                    && w >= MinWidth && h >= MinHeight
                    // Still on a screen that is there (a second monitor may be gone).
                    && l + 80 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && l + w > SystemParameters.VirtualScreenLeft + 80
                    && t >= SystemParameters.VirtualScreenTop - 10 && t + 60 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    (Left, Top, Width, Height) = (l, t, w, h);
                    if (f[4] == "max") WindowState = WindowState.Maximized;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // The first time: a narrow column on the right of the screen, full height, like the pane docked in Revit.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Min(480, area.Width * 0.4);
        Height = area.Height;
        Left = area.Right - Width;
        Top = area.Top;
    }

    private void Remember()
    {
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.IsEmpty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PlaceFile)!);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllText(PlaceFile, string.Join(";", b.Left.ToString("0", c), b.Top.ToString("0", c), b.Width.ToString("0", c), b.Height.ToString("0", c),
                WindowState == WindowState.Maximized ? "max" : "normal"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
