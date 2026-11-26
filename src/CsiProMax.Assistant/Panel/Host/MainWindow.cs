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
        // The version that is running, where it cannot be mistaken for one that is only out or downloaded.
        Title = $"{CsiProMax.App.Product} {Update.Updater.CurrentText}";
        MinWidth = 340;
        MinHeight = 420;
        if (Brand.Icon is { } icon) Icon = icon;
        Brand.UseTheme(this);
        Place();
        var layout = new DockPanel();
        var menu = BuildMenu();
        // The menu, and at its right the Update button: there only while a newer version is out or downloaded.
        var top = new DockPanel { LastChildFill = true };
        var update = new Button { Visibility = Visibility.Collapsed, Margin = new Thickness(4, 2, 6, 2), Padding = new Thickness(10, 2, 10, 2), FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(update, "Update CSI ProMax");
        update.Click += (_, _) =>
        {
            // Downloads when needed (the update window shows it), then this window switches by itself when no work is running.
            CsiProMax.App.CheckForUpdatesInteractive();
            CsiProMax.App.SwitchWhenIdle();
        };
        void ShowUpdate()
        {
            string? text;
            try { text = UpdateBadge.TopButton(Update.Updater.Current, Update.Updater.Latest, Update.Updater.Staged ?? (Update.Updater.NewestDownloaded(Update.Updater.Current) is { } f ? Path.GetFileName(f) : null)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = null; }
            update.Content = text;
            update.ToolTip = text is null ? null : $"This window runs CSI ProMax {Update.Updater.CurrentText}. Click to move to the newer version now; {Csi.Name} stays open and your model is not touched.";
            update.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
        var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        watch.Tick += (_, _) => ShowUpdate();
        Loaded += (_, _) => { ShowUpdate(); watch.Start(); };
        Closed += (_, _) => watch.Stop();
        DockPanel.SetDock(update, Dock.Right);
        top.Children.Add(update);
        top.Children.Add(menu);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        // The Modeler is one program window: its model (3D view, plan, Draw/Define/Assign menus) fills it, and the chat,
        // when it is on, sits on its right and can be closed (Ahmed, 2026-11-26: the Modeler opened as the chat alone).
        // Beside a CSI program the window is the chat (or, with the chat off, what CSI ProMax does by hand).
        if (CsiProMax.App.Modeler) layout.Children.Add(ModelerBody());
        else layout.Children.Add(CsiProMax.AiMode.On ? Chat.ChatPane.Get() : NoAiPane.Build(CsiProMax.App.Reopened));
        Content = layout;
        SourceInitialized += (_, _) => Handle = new WindowInteropHelper(this).Handle;
        Closing += (_, _) => Remember();
        Closed += (_, _) => { Handle = IntPtr.Zero; _open = null; };
    }

    private WebPane? _view;
    private ColumnDefinition? _chatColumn, _splitColumn;
    private FrameworkElement? _chatSide;
    private MenuItem? _chatToggle;

    /// <summary>True when the Modeler's model view is inside this window.</summary>
    public bool HasModelView => _view is not null;

    /// <summary>True when the chat is shown beside the Modeler's model (the field test and the menu read it).</summary>
    public bool ChatShown => _chatSide is { Visibility: Visibility.Visible };

    /// <summary>The chat's width beside the Modeler's model, the first time.</summary>
    internal const double ModelerChatWidth = 420;

    /// <summary>The Modeler's body: the model view, and the chat on its right (with the chat on).</summary>
    private FrameworkElement ModelerBody()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        FrameworkElement model;
        try
        {
            var server = CsiProMax.Bridge.ChatMcp.Ensure() ?? throw new InvalidOperationException("The model view could not start on this PC (no local port).");
            if (!WebWindow.Available()) throw new InvalidOperationException("this PC has no WebView2 Runtime");
            _view = new WebPane(server.ViewUrl, WebPages.Kind.ModelerView);
            _view.Failed += () => { _view = null; };
            model = _view;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No WebView2 here: the model opens in Edge's app window, and this window says so with a button to open it again.
            var note = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            note.Children.Add(Brand.Text("The model view could not be drawn inside this window (" + ex.Message + "). It opens in a window of its own instead.", Brand.K.Text, 13));
            var again = Brand.Button("Open the model view", Brand.Weight.Primary);
            again.Margin = new Thickness(0, 12, 0, 0);
            again.HorizontalAlignment = HorizontalAlignment.Left;
            again.Click += (_, _) => { try { ModelView.Open(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { TaskDialog.Show("CSI ProMax", e.Message); } };
            note.Children.Add(again);
            model = note;
            ContentRendered += (_, _) => { try { ModelView.Open(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } };
        }
        Grid.SetColumn(model, 0);
        grid.Children.Add(model);
        if (!CsiProMax.AiMode.On) return grid;

        _splitColumn = new ColumnDefinition { Width = GridLength.Auto };
        _chatColumn = new ColumnDefinition { Width = new GridLength(ModelerChatWidth), MinWidth = 300 };
        grid.ColumnDefinitions.Add(_splitColumn);
        grid.ColumnDefinitions.Add(_chatColumn);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = System.Windows.Input.Cursors.SizeWE };
        splitter.SetResourceReference(BackgroundProperty, Brand.K.Border);
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var side = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(8, 4, 4, 4) };
        var close = Brand.Button("\u2715", Brand.Weight.Secondary);
        close.Padding = new Thickness(8, 1, 8, 1);
        close.ToolTip = "Close the chat (View > Show the chat brings it back)";
        System.Windows.Automation.AutomationProperties.SetName(close, "Close the chat");
        close.Click += (_, _) => ShowChat(false);
        DockPanel.SetDock(close, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(Brand.Text("Chat", Brand.K.Text, 12.5, bold: true));
        DockPanel.SetDock(bar, Dock.Top);
        side.Children.Add(bar);
        side.Children.Add(Chat.ChatPane.Get());
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);
        _chatSide = side;
        splitter.Tag = side;
        // Kept as the user left it.
        if (File.Exists(ChatClosedFile)) ShowChat(false, remember: false);
        return grid;
    }

    private static string ChatClosedFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CsiProMax", "modeler-chat-closed.txt");

    /// <summary>Shows or closes the chat beside the Modeler's model; the model takes the room.</summary>
    internal void ShowChat(bool show, bool remember = true)
    {
        if (_chatSide is null || _chatColumn is null || _splitColumn is null) return;
        _chatSide.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_chatSide.Parent is Grid grid)
            foreach (var child in grid.Children.OfType<GridSplitter>()) child.Visibility = _chatSide.Visibility;
        _chatColumn.MinWidth = show ? 300 : 0;
        _chatColumn.Width = show ? new GridLength(ModelerChatWidth) : new GridLength(0);
        if (_chatToggle is not null) _chatToggle.IsChecked = show;
        if (!remember) return;
        try
        {
            if (show) File.Delete(ChatClosedFile);
            else { Directory.CreateDirectory(Path.GetDirectoryName(ChatClosedFile)!); File.WriteAllText(ChatClosedFile, "closed"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        if (!CsiProMax.AiMode.Forced)
            Add(app, CsiProMax.AiMode.On ? "Turn the AI chat _off…" : "Turn the AI chat _on…", () => SwitchAi(!CsiProMax.AiMode.On));
        Add(app, "_About", () => TaskDialog.Show("CSI ProMax",
            $"CSI ProMax {Update.Updater.CurrentText}\n{Csi.Name} {(CsiProMax.App.ETABSYear.Length > 0 ? CsiProMax.App.ETABSYear : "not answering yet")}\n" +
            $"Model: {ModelWatch.Current?.Title ?? "none"}\n\n{Update.Updater.Describe()}"));
        app.Items.Add(new Separator());
        Add(app, "_Close CSI ProMax", Close);

        if (CsiProMax.AiMode.On)
        {
            var chat = Top("_Chat");
            Add(chat, "_Notes…", OpenNotes);
            Add(chat, "_Routines…", Chat.ChatPane.OpenRoutinesWindow);
            Add(chat, "_Team performance…", () => { var w = new CrewPerformanceWindow(); Brand.Prepare(w); w.Show(); });
        }

        var view = Top("_View");
        if (CsiProMax.App.Modeler && CsiProMax.AiMode.On)
        {
            _chatToggle = new MenuItem { Header = "Show the _chat", IsCheckable = true, IsChecked = true };
            _chatToggle.Click += (_, _) => ShowChat(_chatToggle.IsChecked);
            view.Items.Add(_chatToggle);
        }
        if (CsiProMax.App.Modeler) Add(view, "_Reload the model view", () => { if (_view is { } pane) pane.Reload(); else ModelView.Open(); });
        else Add(view, "Open the _3D view", ModelView.Open);
        Add(view, "Open the _Viewer (a pack someone sent)", () => WebPages.Open(Shortcuts.WriteViewerPage(), WebPages.Kind.Viewer));
        if (CsiProMax.App.Modeler)
            Add(view, "Open the _models folder", () =>
            {
                Directory.CreateDirectory(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CsiProMax.Assistant.Etabs.OfflineEtabs.DefaultFolder) { UseShellExecute = true })?.Dispose();
            });
        else
        {
            view.Items.Add(new Separator());
            Add(view, $"Open the _Modeler (no {Csi.Name} needed)", ModelView.StartModeler);
        }
        Add(view, "Make the Modeler and Viewer _shortcuts again", () => TaskDialog.Show("CSI ProMax", "The Modeler and Viewer shortcuts are on your desktop and in the Start menu (CSI ProMax):\n" + string.Join("\n", Shortcuts.MakeAll())));

        var help = Top("_Help");
        Add(help, "_Report a bug…", () => { if (CsiProMax.App.Etabs is { } etabs) Bugs.BugReport.Command(etabs); });
        Add(help, "Open the _Bugs folder", () => Bugs.BugReport.OpenFolder());
        if (CsiProMax.AiMode.On) Add(help, "_Export chats and bugs", ExportChatsAndBugs);
        Add(help, "Open _folders…", () => UserFoldersWindow.Open());
        Add(help, "Self _test", RunSelfTest);
        Add(help, "_Tools menu of each CSI program: check and repair", CheckToolsMenus);
        help.Items.Add(new Separator());
        Add(help, "_Field test: everything (saves a report to send)…", () => RunFieldTest(full: true));
        Add(help, "Field test: only what is _new, changed or failed…", () => RunFieldTest(full: true, retest: true));
        // One program alone (Ahmed, 2026-11-26: "an option to just test sap etabs safe one by one").
        foreach (var program in CsiProMax.Assistant.FieldTest.Installed(CsiProMax.App.Etabs?.Link.Program ?? CsiProMax.Assistant.Etabs.CsiProgram.Etabs))
            Add(help, $"Field test: {program.Name} only…", () => RunFieldTest(full: true, only: program));
        Add(help, "Field test, _quick: this PC and the open model", () => RunFieldTest(full: false));
        return menu;
    }

    /// <summary>
    /// Turns the AI chat on or off on this PC (AiMode) and starts this version again with it. The CSI program and the
    /// model stay open; the chats are kept and come back when the chat is turned on again.
    /// </summary>
    internal static void SwitchAi(bool on)
    {
        if (CsiProMax.App.IsBusy)
        {
            TaskDialog.Show("CSI ProMax", "CSI ProMax is working on something. Wait for it to finish (or stop it), then switch the AI chat.");
            return;
        }
        if (CsiProMax.App.Etabs?.Link is CsiProMax.Assistant.Etabs.OfflineEtabs { Unsaved: true })
        {
            TaskDialog.Show("CSI ProMax", "The Modeler's model has changes that are not saved. Save it first (File > Save in the 3D view), then switch the AI chat.");
            return;
        }
        var text = on
            ? $"Turn the AI chat on?\n\nCSI ProMax starts again with the chat, which runs on Claude Code on your own Claude plan (it offers to install it or sign in). {Csi.Name} and your model stay open."
            : $"Turn the AI chat off on this PC?\n\nCSI ProMax starts again without it: the Modeler, the 3D view, review packs, the Viewer and updates all stay. Your chats are kept and come back when you turn it on again. {Csi.Name} and your model stay open.";
        if (TaskDialog.Show("CSI ProMax", text, TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No) != TaskDialogResult.Yes) return;
        try { CsiProMax.AiMode.Save(on); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TaskDialog.Show("CSI ProMax", "The choice could not be saved: " + ex.Message); return; }
        if (CsiProMax.App.Restart is { } restart) restart();
        else TaskDialog.Show("CSI ProMax", $"Saved. Close CSI ProMax and open it again from {Csi.Name}'s Tools menu to use it.");
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

    /// <summary>Looks at each CSI program's plug-in list, adds CSI ProMax where it is missing and may be added, and says what it found.</summary>
    private async void CheckToolsMenus()
    {
        IReadOnlyList<CsiProMax.Assistant.ToolsMenu.Entry> entries;
        try { entries = await Task.Run(() => CsiProMax.Assistant.ToolsMenu.Repair()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("CSI ProMax", "The Tools menus could not be looked at: " + ex.Message); return; }
        TaskDialog.Show("CSI ProMax", "Tools > CSI ProMax in each CSI program of this Windows user:\n\n" + CsiProMax.Assistant.ToolsMenu.Summary(entries));
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
    /// The field test, saved as one report for the person to send back. The full test runs every tool: on a model the
    /// Modeler builds, then in a second window of each CSI program on this PC that it opens on its own scratch model and
    /// closes again without saving. The open model is only read. It runs in the background; a message says when it is done.
    /// </summary>
    private async void RunFieldTest(bool full, bool retest = false, CsiProMax.Assistant.Etabs.CsiProgram? only = null)
    {
        if (CsiProMax.App.Etabs is not { } etabs) return;
        if (CsiProMax.Assistant.SelfTest.IsRunning || CsiProMax.Assistant.FieldTest.IsRunning)
        {
            var at = CsiProMax.Assistant.FieldTest.Last is { Task.IsCompleted: false } run ? $"\n\nIt started at {run.Started:HH:mm} and is at: {run.Doing}" : "";
            TaskDialog.Show("CSI ProMax", "A test is already running. A message says when it is done." + at);
            return;
        }
        if (full)
        {
            var programs = only is { } one ? [one] : CsiProMax.Assistant.FieldTest.Installed(etabs.Link.Program);
            var which = programs.Count == 0
                ? "No ETABS, SAP2000 or SAFE was found on this PC, so the tools inside the programs will be listed as not tested." // ETABS itself
                : $"Then it opens a second window of {string.Join(", ", programs.Select(p => p.Name))} on that test model, runs every tool and check there " +
                  "(the analysis and design too), and closes each window again without saving. Each window takes a licence as usual.";
            if (TaskDialog.Show("CSI ProMax",
                    (retest ? "This field test runs only what is new, changed or failed: a program's part, or CSI ProMax's own features, is left out when all of it passed on this PC and its code is unchanged since. The report names what was left out. Otherwise it is the full test.\n\n" : "") +
                    (only is { } alone ? $"This field test checks {alone.Name} alone: this PC and the install, your open model (reading only), builds the test model with the Modeler, then opens {alone.Name} on it. The other programs are left out. "
                        : "The full field test checks everything CSI ProMax does. It checks this PC and the install, reads your open model, and builds a test model of its own with every Modeler tool. ") +
                    which + "\n\nYour open model is only read: nothing in it is changed. It can take up to an hour. While it runs, leave the windows it opens alone, " +
                    "and wait for it before you use the chat. A message says when the report is ready.\n\nStart it now?",
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogResult.No) != TaskDialogResult.Yes) return;
            // It says on the screen when it is done, the same way whether the menu or the chat started it.
            try { CsiProMax.Tools.FieldTestRuns.StartFull(etabs.Link, "the Help menu", retest, only); }
            catch (InvalidOperationException ex) { TaskDialog.Show("CSI ProMax", ex.Message); }
            return;
        }
        CsiProMax.Assistant.FieldTest.Outcome outcome;
        try { outcome = await Task.Run(() => CsiProMax.Assistant.FieldTest.Run(etabs.Link, changes: false, "the Help menu")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { TaskDialog.Show("CSI ProMax", "The field test could not run: " + ex.Message); return; }
        TaskDialog.Show("CSI ProMax", outcome.Summary());
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{outcome.Send}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Brings the window forward (ETABS's menu was used again while it is open).</summary>
    public void Surface()
    {
        if (WindowState == WindowState.Minimized) WindowState = CsiProMax.App.Modeler ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        // The Modeler is a program: it opens full screen, every time (its window is not the narrow chat column).
        if (CsiProMax.App.Modeler)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Math.Min(1400, area.Width * 0.9);
            Height = Math.Min(900, area.Height * 0.9);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            WindowState = WindowState.Maximized;
            return;
        }
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
        if (CsiProMax.App.Modeler) return; // the Modeler always opens full screen; window.txt is the chat column beside a program
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
