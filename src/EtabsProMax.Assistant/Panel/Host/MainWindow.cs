using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EtabsProMax.UI;

namespace EtabsProMax.Host;

/// <summary>
/// The ETABS ProMax window: Sketch ProMax's chat pane in a window of its own beside ETABS (ETABS has no dockable panes
/// for plug-ins). It can be resized and maximised, remembers where it was, and opens narrow on the right of the screen
/// the first time, like the pane in Revit. Closing it ends ETABS ProMax; ETABS and the model are not touched.
/// </summary>
internal sealed class MainWindow : Window
{
    private static MainWindow? _open;

    /// <summary>This window's handle (zero before it is shown): the owner of ETABS ProMax's dialogs and tool windows.</summary>
    public static IntPtr Handle { get; private set; }

    public static MainWindow? Current => _open;

    private static string PlaceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtabsProMax", "window.txt");

    public MainWindow()
    {
        _open = this;
        Title = EtabsProMax.App.Product;
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
        System.Windows.Automation.AutomationProperties.SetName(menu, "ETABS ProMax menu");
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
                    TaskDialog.Show("ETABS ProMax", $"That didn't work: {ex.Message}");
                }
            };
            parent.Items.Add(item);
        }
        void Dialog(Window window) { Brand.Prepare(window); window.ShowDialog(); }

        var app = Top("_ETABS ProMax");
        Add(app, "_License…", () => LicenseWindow.Open());
        if (Licensing.LicenseState.IsOwnerPc) Add(app, "License _manager…", () => LicenseManagerWindow.Open());
        Add(app, "_Settings…", () => Dialog(new SettingsWindow()));
        Add(app, "Check for _updates", EtabsProMax.App.CheckForUpdatesInteractive);
        Add(app, "_About", () => TaskDialog.Show("ETABS ProMax",
            $"ETABS ProMax {Update.Updater.CurrentText}\nETABS {(EtabsProMax.App.ETABSYear.Length > 0 ? EtabsProMax.App.ETABSYear : "not answering yet")}\n" +
            $"Model: {ModelWatch.Current?.Title ?? "none"}\n\n{Update.Updater.Describe()}"));
        app.Items.Add(new Separator());
        Add(app, "_Close ETABS ProMax", Close);

        var chat = Top("_Chat");
        Add(chat, "_Notes…", OpenNotes);
        Add(chat, "_Routines…", Chat.ChatPane.OpenRoutinesWindow);
        Add(chat, "_Team performance…", () => { var w = new CrewPerformanceWindow(); Brand.Prepare(w); w.Show(); });

        var view = Top("_View");
        Add(view, "Open the _3D view", ModelView.Open);
        if (EtabsProMax.App.Modeler)
            Add(view, "Open the _models folder", () =>
            {
                Directory.CreateDirectory(EtabsProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EtabsProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder) { UseShellExecute = true })?.Dispose();
            });
        else
        {
            view.Items.Add(new Separator());
            Add(view, "Open the _Modeler (no ETABS needed)", ModelView.StartModeler);
        }
        Add(view, "Put a Modeler _shortcut on the desktop", () => TaskDialog.Show("ETABS ProMax", $"The shortcut is on your desktop:\n{ModelView.MakeModelerShortcut()}\n\nIt starts the Modeler with no ETABS open."));

        var help = Top("_Help");
        Add(help, "_Report a bug…", () => { if (EtabsProMax.App.Etabs is { } etabs) Bugs.BugReport.Command(etabs); });
        Add(help, "Open the _Bugs folder", () => Bugs.BugReport.OpenFolder());
        Add(help, "_Export chats and bugs", ExportChatsAndBugs);
        Add(help, "Open _folders…", () => UserFoldersWindow.Open());
        Add(help, "Self _test", RunSelfTest);
        return menu;
    }

    private static Window? _notes;

    private void OpenNotes()
    {
        if (_notes is { } open) { open.Activate(); return; }
        var view = Chat.NotesView.Get();
        if (view.Parent is Window old) old.Content = null;
        var window = new Window { Title = "ETABS ProMax: Notes", Width = 520, Height = 640, Content = view, Owner = this };
        if (Brand.Icon is { } icon) window.Icon = icon;
        Brand.UseTheme(window);
        window.Closed += (_, _) => { window.Content = null; _notes = null; };
        _notes = window;
        window.Show();
    }

    private static void ExportChatsAndBugs()
    {
        if (EtabsProMax.App.Etabs is not { } etabs) return;
        var result = System.Text.Json.JsonSerializer.Serialize(Bugs.ChatsAndBugsExport.Export(etabs, "week"));
        var file = Chat.ChatFiles.Find(result).FirstOrDefault();
        TaskDialog.Show("ETABS ProMax", file is null
            ? "The export of this week's chats and bugs was made in " + Bugs.ChatsAndBugsExport.Folder + "."
            : "This week's chats and bugs were exported to:\n" + file);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Bugs.ChatsAndBugsExport.Folder}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void RunSelfTest()
    {
        if (EtabsProMax.App.Etabs is not { } etabs) return;
        if (EtabsProMax.Assistant.SelfTest.IsRunning) { TaskDialog.Show("ETABS ProMax", "A self test is already running."); return; }
        List<EtabsProMax.Assistant.SelfTest.StepResult> steps;
        try { steps = await Task.Run(() => EtabsProMax.Assistant.SelfTest.Steps(etabs.Link, changes: false)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("ETABS ProMax", "The self test could not run: " + ex.Message); return; }
        var bad = steps.Where(s => !s.Ok).ToList();
        TaskDialog.Show("ETABS ProMax", bad.Count == 0
            ? $"Self test: all {steps.Count} checks passed. ETABS ProMax can read the open model. Nothing was changed."
            : $"Self test: {steps.Count - bad.Count} of {steps.Count} checks passed. Not working:\n" + string.Join("\n", bad.Select(s => $"- {s.Name}: {s.Detail}")));
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
