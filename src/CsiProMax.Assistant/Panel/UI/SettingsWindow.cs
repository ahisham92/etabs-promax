using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using CsiProMax.Settings;

namespace CsiProMax.UI;

/// <summary>
/// Settings dialog, in tabs (<see cref="SettingsLayout"/>): Approvals, Chat, Firm standards, Runs and Updates. The window fits the monitor ETABS is on, each tab's page scrolls on its own
/// and the Save / Cancel bar stays pinned at the bottom. The tab opened last is opened again.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AgentSettings _settings = AgentSettings.Load();
    private readonly List<(RadioButton Button, Tools.ApprovalLevel Level)> _levels = [];
    private readonly List<(RadioButton Button, Chat.Motion Level)> _motion = [];
    private readonly List<(RadioButton Button, string Type)> _chatTypes = [];
    private readonly Dictionary<string, ComboBox> _picks = new(StringComparer.Ordinal);
    private readonly List<string> _noAsk;
    private readonly StackPanel _noAskList = new();
    private readonly CheckBox _autoUpdate = Check("Update CSI ProMax automatically");
    private readonly CheckBox _keepOpen = Check("Keep the CSI program open with the analysed model after a Run (it holds its licence until you close it)");
    private readonly TextBox _standards = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly TextBlock _error = Brand.Text("", Brand.K.DangerText);
    private readonly TextBox _phoneNames = new() { MinHeight = 26 };
    private readonly TextBox _crewYou = new() { MinHeight = 26 }, _crewCoordinator = new() { MinHeight = 26 }, _crewWorkers = new() { MinHeight = 26 }, _crewApplier = new() { MinHeight = 26 };
    private readonly ComboBox _jobLimit = new() { MinHeight = 26, MinWidth = 56, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _jobBudget = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _jobReview = new() { MinHeight = 26, MinWidth = 200, Margin = new Thickness(6, 0, 6, 0) };
    private readonly CheckBox _autoSync = Check("Synchronize with central after every");
    private readonly CheckBox _autopilotWeekly = Check("When a weekly limit pauses the autopilot, carry on by itself when the week resets");
    private readonly ComboBox _apJob = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _apFive = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _apWeek = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _apAsk = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ComboBox _apUpdate = new() { MinHeight = 26, MinWidth = 64, Margin = new Thickness(6, 0, 6, 0) };
    private static readonly int[] FiveHourChoices = [50, 60, 70, 75, 80, 85, 90, 95, 100];
    private static readonly int[] WeekChoices = [0, 5, 10, 15, 20, 25, 30, 40, 50, 75, 100];
    private readonly TextBox _syncChanges = new() { Width = 64, MinHeight = 26, Margin = new Thickness(6, 0, 6, 0) };
    private readonly TabControl _tabs = new();
    private readonly List<(string Setting, FrameworkElement Control)> _placed = [];

    /// <param name="openTab">The tab to open on (a SettingsLayout key); null: the one used last.</param>
    public SettingsWindow(string? openTab = null)
    {
        Title = "CSI ProMax settings";
        var area = ETABSWorkArea();
        (Width, Height, MinWidth, MinHeight) = SettingsLayout.Fit(area.Width, area.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Brand.UseTheme(this);

        _noAsk = [.. _settings.NoAskTools ?? []];
        _autoUpdate.IsChecked = _settings.AutoUpdate;
        _keepOpen.IsChecked = _settings.KeepProgramOpenAfterRun;
        _standards.Text = _settings.FirmStandards;
        _phoneNames.Text = _settings.PhoneProjectNames ?? "";
        for (var n = Chat.JobBoard.MinLimit; n <= Chat.JobBoard.MaxLimit; n++) _jobLimit.Items.Add(new ComboBoxItem { Content = n.ToString(System.Globalization.CultureInfo.CurrentCulture), Tag = n });
        _jobLimit.SelectedIndex = Chat.JobBoard.ClampLimit(_settings.ChatJobLimit) - Chat.JobBoard.MinLimit;
        foreach (var p in Chat.JobBudget.Choices)
            _jobBudget.Items.Add(new ComboBoxItem { Content = p == 0 ? "never" : p.ToString(System.Globalization.CultureInfo.CurrentCulture) + " %", Tag = p });
        _jobBudget.SelectedIndex = Math.Max(0, Array.IndexOf(Chat.JobBudget.Choices, _settings.ChatJobBudget) is var b and >= 0 ? b : Array.IndexOf(Chat.JobBudget.Choices, Chat.JobBudget.DefaultPercent));
        foreach (var (tag, text) in new[] { (Chat.JobReview.Always, "every finished job"), (Chat.JobReview.AutopilotOnly, "only in an autopilot"), (Chat.JobReview.Off, "no job") })
            _jobReview.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
        _jobReview.SelectedIndex = Chat.JobReview.Normal(_settings.ChatJobReview) switch { Chat.JobReview.AutopilotOnly => 1, Chat.JobReview.Off => 2, _ => 0 };
        _autoSync.IsChecked = _settings.AutoSync;
        _autopilotWeekly.IsChecked = _settings.AutopilotResumeWeekly;
        Fill(_apJob, Chat.JobBudget.Choices, _settings.AutopilotJobBudget, Chat.JobBudget.DefaultPercent, "never");
        Fill(_apFive, FiveHourChoices, _settings.AutopilotFiveHourMax, 90, null);
        Fill(_apWeek, WeekChoices, _settings.AutopilotWeekShare, 25, "no cap");
        foreach (var m in Chat.AskRules.WaitChoices)
            _apAsk.Items.Add(new ComboBoxItem { Content = m == 0 ? "never (ask nothing)" : m.ToString(System.Globalization.CultureInfo.CurrentCulture) + " min", Tag = m });
        foreach (var m in Chat.AskRules.UpdateChoices)
            _apUpdate.Items.Add(new ComboBoxItem { Content = m == 0 ? "never" : m < 60 ? m.ToString(System.Globalization.CultureInfo.CurrentCulture) + " min" : (m / 60).ToString(System.Globalization.CultureInfo.CurrentCulture) + (m == 60 ? " hour" : " hours"), Tag = m });
        _apUpdate.SelectedIndex = Math.Max(0, Array.IndexOf(Chat.AskRules.UpdateChoices, _settings.PhoneUpdateMinutes) is var u and >= 0 ? u
            : Array.IndexOf(Chat.AskRules.UpdateChoices, Chat.AskRules.DefaultUpdateMinutes));
        _apAsk.SelectedIndex = Math.Max(0, Array.IndexOf(Chat.AskRules.WaitChoices, _settings.AutopilotAskWaitMinutes) is var i and >= 0 ? i
            : Array.IndexOf(Chat.AskRules.WaitChoices, Chat.AskRules.DefaultWaitMinutes));
        _syncChanges.Text = ETABS.AutoSyncRule.Clamp(_settings.AutoSyncChanges).ToString(System.Globalization.CultureInfo.CurrentCulture);

        // One primary action: Save (Enter, except inside the standards box where Enter starts a new line). Esc cancels.
        var save = Brand.Button("Save", Brand.Weight.Primary);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        var cancel = Brand.Button("Cancel");
        cancel.IsCancel = true;

        TabStyles(_tabs);
        AutomationProperties.SetName(_tabs, "Settings sections");
        foreach (var tab in SettingsLayout.Tabs)
        {
            var page = tab.Key switch
            {
                SettingsLayout.Approvals => ApprovalPage(),
                SettingsLayout.Chat => ChatPage(),
                SettingsLayout.Standards => StandardsPage(),
                SettingsLayout.Runs => RunsPage(),
                _ => UpdatesPage(),
            };
            page.Children.Insert(0, Heading(tab.Name));
            var intro = Brand.Text(tab.Intro, Brand.K.Muted);
            intro.Margin = new Thickness(0, 2, 0, 4);
            page.Children.Insert(1, intro);
            page.Margin = new Thickness(20, 14, 20, 16);
            var item = new TabItem
            {
                Header = tab.Name, Tag = tab.Key,
                // Each page scrolls on its own; long lines wrap instead of scrolling sideways.
                Content = new ScrollViewer
                {
                    Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
                },
            };
            AutomationProperties.SetName(item, tab.Name);
            AutomationProperties.SetHelpText(item, tab.Intro);
            // With the AI chat off (AiMode) only Runs and Updates are shown: the other pages are about the chat. They are
            // still made, so saving keeps what they hold. (The Engine's Run works without the AI.)
            if (!CsiProMax.AiMode.On && tab.Key is not (SettingsLayout.Updates or SettingsLayout.Runs)) continue;
            _tabs.Items.Add(item);
        }
        _tabs.SelectedIndex = CsiProMax.AiMode.On ? SettingsLayout.StartIndex(openTab ?? ReadLastTab()) : 0;
        Closed += (_, _) => WriteLastTab((_tabs.SelectedItem as TabItem)?.Tag as string);

        // The bar at the foot: always visible, whatever the size of the window or the page.
        var note = Brand.Text("Changes apply to new chats; chat animations and the number of jobs change straight away.", Brand.K.Muted, size: 11);
        _error.Margin = new Thickness(0, 6, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        var buttons = Brand.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        var foot = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12),
            Child = new StackPanel { Children = { note, _error, buttons } },
        };
        foot.SetResourceReference(Border.BorderBrushProperty, Brand.K.Border);

        var root = new DockPanel();
        var header = Brand.Header("Settings", $"How CSI ProMax works in this {Csi.Name}.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(_tabs);
        Content = root;

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => KeepOnScreen();
        Loaded += (_, _) => FocusPage();
    }

    /// <summary>Every setting control with the AgentSettings property it edits, for the field test's check that each is on its tab.</summary>
    internal IReadOnlyList<(string Setting, FrameworkElement Control)> Placed => _placed;

    /// <summary>The tab list, for the field test.</summary>
    internal TabControl Tabs => _tabs;

    private T Place<T>(string setting, T control) where T : FrameworkElement
    {
        _placed.Add((setting, control));
        return control;
    }

    /// <summary>A percent picker: <paramref name="choices"/> (0 shown as <paramref name="zero"/>), on <paramref name="value"/> or the default.</summary>
    private static void Fill(ComboBox box, int[] choices, int value, int fallback, string? zero)
    {
        foreach (var p in choices)
            box.Items.Add(new ComboBoxItem { Content = p == 0 && zero is not null ? zero : p.ToString(System.Globalization.CultureInfo.CurrentCulture) + " %", Tag = p });
        var at = Array.IndexOf(choices, value);
        box.SelectedIndex = at >= 0 ? at : Math.Max(0, Array.IndexOf(choices, fallback));
    }

    private static int Picked(ComboBox box, int fallback) => (box.SelectedItem as ComboBoxItem)?.Tag is int p ? p : fallback;

    /// <summary>"before [picker] after" on one line (the picker already placed for its setting).</summary>
    private WrapPanel LimitRow(string before, ComboBox box, string after, string help)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var a = Brand.Text(before);
        a.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(a);
        AutomationProperties.SetName(box, before + " … " + after);
        AutomationProperties.SetHelpText(box, help);
        row.Children.Add(box);
        var b = Brand.Text(after);
        b.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(b);
        return row;
    }

    private static CheckBox Check(string label)
    {
        // A wrapping text block, so a long label wraps inside the page instead of being cut off on the right.
        var box = new CheckBox { Content = Brand.Text(label), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(box, label);
        return box;
    }

    private static TextBlock Heading(string text)
    {
        var heading = Brand.Text(text, size: 15, bold: true);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
#endif
        return heading;
    }

    private static TextBlock SubHeading(string text)
    {
        var heading = Brand.Text(text, bold: true);
        heading.Margin = new Thickness(0, 14, 0, 2);
#if !NETFRAMEWORK
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
#endif
        return heading;
    }

    private static TextBlock Hint(string text, double top = 6)
    {
        var hint = Brand.Text(text, Brand.K.Muted, size: 11);
        hint.Margin = new Thickness(0, top, 0, 0);
        return hint;
    }

    // ---- Pages ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// When ETABS asks before CSI ProMax changes something: the levels, what is always asked (code, deletes that take
    /// other elements, overwriting files), and the tools the user said not to ask about again,
    /// each with "Ask again".
    /// </summary>
    private StackPanel ApprovalPage()
    {
        var page = new StackPanel();
        page.Children.Add(SubHeading("Ask before changes"));
        var current = _settings.Approvals;
        foreach (var level in Tools.ApprovalPolicy.Levels)
        {
            var radio = new RadioButton
            {
                GroupName = "approvals", IsChecked = level == current, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Tools.ApprovalPolicy.Label(level) + (level == Tools.ApprovalLevel.RiskyOnly ? " (recommended)" : "")),
                        Brand.Text(Tools.ApprovalPolicy.Hint(level) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, Tools.ApprovalPolicy.Label(level));
            AutomationProperties.SetHelpText(radio, Tools.ApprovalPolicy.Hint(level));
            _levels.Add((radio, level));
            page.Children.Add(Place("ApprovalLevel", radio));
        }
        page.Children.Add(Hint(
            $"{Csi.Name} has no undo for what CSI ProMax changes, so these are asked at the first three levels, whatever you choose: " +
            "code CSI ProMax wrote, deletes, saving the model, running the analysis, and overwriting a file outside " +
            "Documents\\CSI ProMax. Always allow runs those too, without asking. A brief you run counts as approving its task."));
        page.Children.Add(SubHeading("Not asked again"));
        page.Children.Add(Place("NoAskTools", _noAskList));
        FillNoAsk();
        return page;
    }

    /// <summary>The tools picked with "Don't ask again", each with Ask again, or a line saying there are none.</summary>
    private void FillNoAsk()
    {
        _noAskList.Children.Clear();
        if (_noAsk.Count == 0)
        {
            _noAskList.Children.Add(Brand.Text("None. In an approval dialog, \"Don't ask again\" adds a tool here.", Brand.K.Muted, size: 11));
            return;
        }
        foreach (var tool in _noAsk.OrderBy(t => t, StringComparer.Ordinal).ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var again = Brand.Button("Ask again");
            again.MinHeight = 24;
            AutomationProperties.SetName(again, "Ask again for " + Tools.ApprovalPolicy.Friendly(tool));
            again.Click += (_, _) =>
            {
                _noAsk.Remove(tool);
                FillNoAsk();
                (_noAskList.Children.OfType<DockPanel>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault() as UIElement ?? _levels[0].Button).Focus();
            };
            DockPanel.SetDock(again, Dock.Right);
            row.Children.Add(again);
            var name = Brand.Text(Tools.ApprovalPolicy.Friendly(tool));
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            _noAskList.Children.Add(row);
        }
        if (_noAsk.Count > 1)
        {
            var all = Brand.Button("Ask again for all");
            all.HorizontalAlignment = HorizontalAlignment.Left;
            all.Margin = new Thickness(0, 4, 0, 0);
            all.MinHeight = 24;
            all.Click += (_, _) => { _noAsk.Clear(); FillNoAsk(); _levels[0].Button.Focus(); };
            _noAskList.Children.Add(all);
        }
    }

    /// <summary>How much the chat pane moves: Full, Reduced or Off, each with what it means.</summary>
    private StackPanel ChatPage()
    {
        var page = new StackPanel();
        page.Children.Add(SubHeading("Sign-in"));
        page.Children.Add(SignInRow());
        page.Children.Add(SubHeading("Chat animations"));
        var current = Chat.MotionLevels.Parse(_settings.ChatAnimations);
        foreach (var level in Chat.MotionLevels.All)
        {
            var radio = new RadioButton
            {
                GroupName = "motion", IsChecked = level == current, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Chat.MotionLevels.Label(level) + (level == Chat.Motion.Full ? " (recommended)" : "")),
                        Brand.Text(Chat.MotionLevels.Hint(level) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, "Chat animations: " + Chat.MotionLevels.Label(level));
            AutomationProperties.SetHelpText(radio, Chat.MotionLevels.Hint(level));
            _motion.Add((radio, level));
            page.Children.Add(Place("ChatAnimations", radio));
        }
        page.Children.Add(Hint("When Windows' Show animations is off (Settings > Accessibility > Visual effects), the chat doesn't animate whatever you pick."));
        page.Children.Add(SubHeading("New chats"));
        page.Children.Add(Hint("Each chat keeps its own type, shown under its name, where you can switch it while none of its jobs is open. New chat (+) offers both types, this one first.", 2));
        var defaultType = Chat.ChatTypes.Parse(_settings.ChatTypeDefault);
        foreach (var type in Chat.ChatTypes.All)
        {
            var radio = new RadioButton
            {
                GroupName = "chattype", IsChecked = type == defaultType, Margin = new Thickness(0, 6, 0, 0), MinHeight = 24,
                Content = new StackPanel
                {
                    Children =
                    {
                        Brand.Text(Chat.ChatTypes.Label(type) + (type == Chat.ChatTypes.Simple ? " (default)" : "")),
                        Brand.Text(Chat.ChatTypes.Hint(type) + ".", Brand.K.Muted, size: 11),
                    },
                },
            };
            AutomationProperties.SetName(radio, "New chats start as: " + Chat.ChatTypes.Label(type));
            AutomationProperties.SetHelpText(radio, Chat.ChatTypes.Hint(type));
            _chatTypes.Add((radio, type));
            page.Children.Add(Place("ChatTypeDefault", radio));
        }
        page.Children.Add(SubHeading("Model and effort"));
        page.Children.Add(Hint("New chats start on these; the pickers under a chat's name change them for that chat (and its new jobs) until set back to Automatic. Automatic sorts each request and job into simple, everyday or hard work by its words and uses the model and effort below, to save your plan's limits.", 2));
        page.Children.Add(PickRow("New chats' model", "ChatModelDefault", _settings.ChatModelDefault, models: true, automatic: true, b => Place("ChatModelDefault", b)));
        page.Children.Add(PickRow("New chats' effort", "ChatEffortDefault", _settings.ChatEffortDefault, models: false, automatic: true, b => Place("ChatEffortDefault", b)));
        foreach (var kind in Chat.ChatAuto.Kinds.Append(Chat.ChatAuto.Coordinating))
        {
            var label = Brand.Text(Chat.ChatAuto.KindLabel(kind), bold: true);
            label.Margin = new Thickness(0, 10, 0, 0);
            page.Children.Add(label);
            page.Children.Add(Hint(Chat.ChatAuto.KindHint(kind) + ".", 0));
            var what = Chat.ChatAuto.KindLabel(kind);
            var row = new WrapPanel();
            switch (kind)
            {
                case Chat.ChatAuto.Simple:
                    row.Children.Add(PickRow("Model", "ChatModelSimple", _settings.ChatModelSimple, models: true, automatic: false, b => Place("ChatModelSimple", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortSimple", _settings.ChatEffortSimple, models: false, automatic: false, b => Place("ChatEffortSimple", b), what));
                    break;
                case Chat.ChatAuto.Coordinating:
                    row.Children.Add(PickRow("Model", "ChatModelCoordinator", _settings.ChatModelCoordinator, models: true, automatic: false, b => Place("ChatModelCoordinator", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortCoordinator", _settings.ChatEffortCoordinator, models: false, automatic: false, b => Place("ChatEffortCoordinator", b), what));
                    break;
                case Chat.ChatAuto.Hard:
                    row.Children.Add(PickRow("Model", "ChatModelHard", _settings.ChatModelHard, models: true, automatic: false, b => Place("ChatModelHard", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortHard", _settings.ChatEffortHard, models: false, automatic: false, b => Place("ChatEffortHard", b), what));
                    break;
                default:
                    row.Children.Add(PickRow("Model", "ChatModelEveryday", _settings.ChatModelEveryday, models: true, automatic: false, b => Place("ChatModelEveryday", b), what));
                    row.Children.Add(PickRow("Effort", "ChatEffortEveryday", _settings.ChatEffortEveryday, models: false, automatic: false, b => Place("ChatEffortEveryday", b), what));
                    break;
            }
            page.Children.Add(row);
        }
        page.Children.Add(SubHeading("Parallel jobs"));
        page.Children.Add(Hint("A chat With coordinator can split a request into jobs that work side by side, each shown as a card that opens the job's own chat. Reads run together; changes to the model take turns, one at a time.", 2));
        var jobRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var before = Brand.Text("Run up to");
        before.VerticalAlignment = VerticalAlignment.Center;
        jobRow.Children.Add(before);
        AutomationProperties.SetName(_jobLimit, "How many jobs run at once");
        AutomationProperties.SetHelpText(_jobLimit, "Each job uses your plan while it works; more jobs wait to start until one finishes. 3 is usual.");
        jobRow.Children.Add(Place("ChatJobLimit", _jobLimit));
        var after = Brand.Text("jobs at once (3 is usual; fewer keeps more of your plan's limits)");
        after.VerticalAlignment = VerticalAlignment.Center;
        jobRow.Children.Add(after);
        page.Children.Add(jobRow);
        var budgetRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var budgetBefore = Brand.Text("Pause a job when it uses");
        budgetBefore.VerticalAlignment = VerticalAlignment.Center;
        budgetRow.Children.Add(budgetBefore);
        AutomationProperties.SetName(_jobBudget, "Pause a job after this share of the 5-hour limit");
        AutomationProperties.SetHelpText(_jobBudget, "A job that uses this much of your plan's 5-hour limit pauses with a warning; send it a message to let it carry on. 15 % is usual.");
        budgetRow.Children.Add(Place("ChatJobBudget", _jobBudget));
        var budgetAfter = Brand.Text("of the 5-hour limit (stops a job going round in circles; 15 % is usual)");
        budgetAfter.VerticalAlignment = VerticalAlignment.Center;
        budgetRow.Children.Add(budgetAfter);
        page.Children.Add(budgetRow);
        var reviewRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var reviewBefore = Brand.Text("A second worker checks");
        reviewBefore.VerticalAlignment = VerticalAlignment.Center;
        reviewRow.Children.Add(reviewBefore);
        AutomationProperties.SetName(_jobReview, "Which finished jobs a second worker checks");
        AutomationProperties.SetHelpText(_jobReview, "Before a job is reported done, another worker reads the model to check the work against the task, without changing anything.");
        reviewRow.Children.Add(Place("ChatJobReview", _jobReview));
        var reviewAfter = Brand.Text("in the model before it is reported done");
        reviewAfter.VerticalAlignment = VerticalAlignment.Center;
        reviewRow.Children.Add(reviewAfter);
        page.Children.Add(reviewRow);
        page.Children.Add(SubHeading("Autopilot"));
        page.Children.Add(LimitRow("One job pauses after", Place("AutopilotJobBudget", _apJob), "of the 5-hour limit (the coordinator then decides)",
            "An autopilot job that uses this much of the 5-hour limit pauses, and the coordinator decides whether it carries on."));
        page.Children.Add(LimitRow("The run pauses at", Place("AutopilotFiveHourMax", _apFive), "of the 5-hour limit, and carries on when the 5 hours reset",
            "When the 5-hour limit reaches this, the autopilot pauses; it carries on by itself when the limit resets."));
        page.Children.Add(LimitRow("One run uses at most", Place("AutopilotWeekShare", _apWeek), "of the weekly limit, then pauses",
            "The run's own use of your weekly limit; when it reaches this the autopilot pauses."));
        page.Children.Add(LimitRow("When a job needs you, ask you and wait", Place("AutopilotAskWaitMinutes", _apAsk), "for a reply, then carry on with its assumption",
            "The question goes to your phone (connect it below), with a button for the assumed answer. A later answer still counts and the work is put right. With no phone connected, nothing is asked: the job makes the call itself."));
        page.Children.Add(LimitRow("Send you a progress update every", Place("PhoneUpdateMinutes", _apUpdate), "while it runs; reply to add or change anything",
            "How many jobs finished, how many are left, and the questions waiting for you, to your phone. What you write back goes to the coordinator as your message."));
        page.Children.Add(SubHeading("Your phone"));
        page.Children.Add(PhoneRow());
        page.Children.Add(Label("Phone chats"));
        AutomationProperties.SetName(_phoneNames, "Names of the project chats on your phone");
        page.Children.Add(Place("PhoneProjectNames", _phoneNames));
        page.Children.Add(Hint("Each project has its own chat on your phone, named after the coordinator and the project, for example \"Sam (Harbour)\". The project name comes from the model's file; to change it, or join two names into one chat, write them here: HBR = Harbour, TWR = Tower.", 2));
        _autopilotWeekly.Margin = new Thickness(0, 8, 0, 0);
        page.Children.Add(Place("AutopilotResumeWeekly", _autopilotWeekly));
        page.Children.Add(Hint("Any pause carries on when you press Resume on the chat's banner or type \"continue\" in the autopilot chat. A weekly limit near full pauses it too.", 2));
        page.Children.Add(SubHeading("Names of the crew"));
        page.Children.Add(Hint($"Over a chat With coordinator, a strip shows the work as people: you hand it to the coordinator, who gives each job to a worker; a worker with a change for the model queues for the one who applies changes in {Csi.Name}, one at a time. Leave a name blank for its default.", 2));
        var names = Chat.CrewNames.From(_settings.ChatCrewYou, _settings.ChatCrewCoordinator, _settings.ChatCrewWorkers, _settings.ChatCrewApplier, Environment.UserName);
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void NameRow(string label, TextBox box, string value, string help)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = Brand.Text(label);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 4, 12, 4);
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            box.Text = value;
            box.Margin = new Thickness(0, 4, 0, 4);
            box.MinWidth = 240;
            AutomationProperties.SetName(box, label);
            AutomationProperties.SetHelpText(box, help);
            box.ToolTip = help;
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
        }
        NameRow("You", Place("ChatCrewYou", _crewYou), names.You, "Your name, on the person who hands over the requests.");
        NameRow("Coordinator", Place("ChatCrewCoordinator", _crewCoordinator), names.Coordinator, "The main chat that hands out the jobs.");
        NameRow("Workers", Place("ChatCrewWorkers", _crewWorkers), string.Join(", ", names.Workers), "One name per job that runs at once, separated by commas. More jobs than names get Worker 4, Worker 5 and so on.");
        NameRow($"Applies changes in {Csi.Name}", Place("ChatCrewApplier", _crewApplier), names.Applier, "Takes each worker's change into the model, one at a time.");
        page.Children.Add(grid);
        page.Children.Add(Hint("The model and Brief before running are chosen in the Chat pane, next to the message box.", 12));
        return page;
    }

    /// <summary>A label and a list of models (Claude Code's aliases) or effort levels, optionally with Automatic first.</summary>
    private FrameworkElement PickRow(string label, string key, string? value, bool models, bool automatic, Func<ComboBox, ComboBox> place, string what = "")
    {
        var box = new ComboBox { MinHeight = 26, MinWidth = 120, Margin = new Thickness(6, 0, 12, 0) };
        if (automatic) box.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = Chat.ChatAuto.Automatic });
        if (models) foreach (var m in Chat.ChatModels.All) box.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id, ToolTip = m.Hint });
        else foreach (var e in Chat.ChatAuto.Efforts) box.Items.Add(new ComboBoxItem { Content = Chat.ChatAuto.EffortLabel(e), Tag = e, ToolTip = Chat.ChatAuto.EffortHint(e) });
        var stored = models ? Chat.ChatAuto.ParseModel(value) : Chat.ChatAuto.ParseEffort(value);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == stored) ?? box.Items[0];
        AutomationProperties.SetName(box, (what.Length > 0 ? what + ": " : "") + label);
        _picks[key] = box;
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var text = Brand.Text(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        row.Children.Add(place(box));
        return row;
    }

    private string Picked(string key, string fallback) => _picks.TryGetValue(key, out var box) && (box.SelectedItem as ComboBoxItem)?.Tag is string v ? v : fallback;

    private static TextBlock Label(string text)
    {
        var label = Brand.Text(text);
        label.Margin = new Thickness(0, 12, 0, 4);
        return label;
    }

    /// <summary>Firm standards in plain English.</summary>
    private StackPanel StandardsPage()
    {
        var page = new StackPanel();
        var standardsLabel = Brand.Text(
            "Firm standards (plain English). CSI ProMax follows these in every conversation, e.g. " +
            "'Concrete is C40 for columns and C30 for slabs. Drift limit is H/500. Cracked stiffness: beams 0.35, columns 0.7, walls 0.35.'");
        standardsLabel.Margin = new Thickness(0, 10, 0, 4);
        page.Children.Add(standardsLabel);
        AutomationProperties.SetName(_standards, "Firm standards");
        AutomationProperties.SetHelpText(_standards, standardsLabel.Text);
        page.Children.Add(Place("FirmStandards", _standards));
        page.Children.Add(Hint("Enter starts a new line here; Ctrl+Enter saves."));

        return page;
    }

    /// <summary>
    /// Settings › Chat › Your phone: whether this PC is connected to the user's phone through Iris (their own website), with
    /// Connect your phone (a QR code to scan) and Disconnect. Connected, the autopilot asks and reports there.
    /// </summary>
    private StackPanel PhoneRow()
    {
        var panel = new StackPanel();
        var state = Brand.Text("");
        state.TextWrapping = TextWrapping.Wrap;
        var connect = Brand.Button("Connect your phone…");
        var disconnect = Brand.Button("Disconnect", Brand.Weight.Danger);
        void Show()
        {
            var on = Phone.PhoneLink.Connected;
            state.Text = on
                ? $"Connected through {Phone.PhoneLink.Site}. Autopilot questions and progress go to your phone, with one-tap answers and Stop/Resume. You can also write to the chat from the phone; the answer comes back there. Switch it off and on with Phone under the chat's message box."
                : "Not connected: the autopilot asks you nothing and sends no progress. Connect your phone to get its questions there, with one-tap answers.";
            connect.Content = on ? "Connect again…" : "Connect your phone…";
            disconnect.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        connect.Click += (_, _) =>
        {
            var window = new PhoneConnectWindow(Phone.PhoneLink.Site) { Owner = this };
            window.Connected += Show;
            window.ShowDialog();
            Show();
        };
        AutomationProperties.SetHelpText(disconnect, "The autopilot stops asking and reporting. You can also disconnect from the phone.");
        disconnect.Click += async (_, _) =>
        {
            disconnect.IsEnabled = false;
            await Task.Run(Phone.PhoneLink.DisconnectAsync);
            Phone.PhoneLink.Notify();
            disconnect.IsEnabled = true;
            Show();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        connect.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(connect);
        buttons.Children.Add(disconnect);
        panel.Children.Add(state);
        panel.Children.Add(buttons);
        Show();
        return panel;
    }

    /// <summary>
    /// Settings › Chat › Sign-in: whether the chat's sign-in really works (an expired one counts as signed out, see
    /// Chat.SignInHealth), and Sign in again, which opens the browser (no Command Prompt).
    /// </summary>
    private StackPanel SignInRow()
    {
        var panel = new StackPanel();
        var state = Brand.Text("Checking the sign-in…");
        state.TextWrapping = TextWrapping.Wrap;
        var signIn = Brand.Button("Sign in again", Brand.Weight.Primary);
        AutomationProperties.SetHelpText(signIn, "Opens your browser to sign in. Use it when the sign-in expired, or to use another account.");
        Chat.ClaudeCli.Install? install = null;
        void Say(string text, string key = Brand.K.Text)
        {
            state.Text = text;
            Brand.Fg(state, key);
        }
        async void Check()
        {
            (Chat.ClaudeCli.Install? found, Chat.ClaudeCli.SignIn signedIn) result;
            try
            {
                result = await Task.Run(() =>
                {
                    var cli = Chat.ClaudeCli.Find();
                    return (cli, cli is null ? Chat.ClaudeCli.SignIn.Unknown : Chat.SignInCheck.Real(Chat.ClaudeCli.CheckSignIn(cli).State));
                });
            }
            catch (Exception ex)
            {
                Say("The sign-in couldn't be checked: " + ex.Message, Brand.K.DangerText);
                return;
            }
            var (found, signedIn) = result;
            install = found;
            if (found is null && Chat.ClaudeCli.LastSearch is { Failed.Count: > 0 })
                Say("Claude Code is on this PC, but CSI ProMax couldn't use it. Open Chat: its card says which file and why.", Brand.K.DangerText);
            else if (found is null) Say("The chat isn't installed on this PC yet. Open Chat: it offers to install it, or to point at it.", Brand.K.Muted);
            else if (Chat.SignInFlow.Now == Chat.SignInFlow.State.Waiting) Say("Finish signing in in your browser…");
            else if (Chat.SignInHealth.Broken) Say("Sign-in expired: CSI ProMax can't use your plan until you sign in again.", Brand.K.DangerText);
            else if (signedIn == Chat.ClaudeCli.SignIn.SignedOut) Say("Signed out. Sign in to chat.", Brand.K.DangerText);
            else Say("Signed in.", Brand.K.SuccessText);
            signIn.IsEnabled = found is not null;
        }
        void Flow()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                switch (Chat.SignInFlow.Now)
                {
                    case Chat.SignInFlow.State.Waiting: Say("Finish signing in in your browser…"); break;
                    case Chat.SignInFlow.State.Done: Check(); break;
                    case Chat.SignInFlow.State.Failed:
                        Say("The sign-in didn't finish" + (Chat.SignInFlow.Problem.Length > 0 ? ": " + Chat.SignInFlow.Problem : ".") + " Press Sign in again.", Brand.K.DangerText);
                        break;
                }
            }));
        }
        Chat.SignInFlow.Changed += Flow;
        Closed += (_, _) => Chat.SignInFlow.Changed -= Flow;
        signIn.Click += (_, _) =>
        {
            if (install is null) { Check(); return; }
            try { Chat.SignInFlow.Start(install); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Chat.ClaudeCli.StartSignIn(install); // a PC that won't run it hidden gets the sign-in window
            }
            Say("Finish signing in in your browser…");
        };
        signIn.Margin = new Thickness(0, 8, 0, 0);
        signIn.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(state);
        panel.Children.Add(signIn);
        Check();
        return panel;
    }

    /// <summary>Runs: whether the Engine's Run leaves the CSI program open after the run (Ahmed 2026-11-27).</summary>
    private StackPanel RunsPage()
    {
        var page = new StackPanel();
        page.Children.Add(Place("KeepProgramOpenAfterRun", _keepOpen));
        page.Children.Add(Hint("On: after Run, the program stays open and shown with the model it analysed and designed, to look at the results there. The next Run closes it first. Off: it is closed as soon as the run is done, so its licence is free at once. The Run form has the same choice."));
        return page;
    }

    /// <summary>Automatic updates, the version installed and the last check, and Check now.</summary>
    private StackPanel UpdatesPage()
    {
        var page = new StackPanel();
        page.Children.Add(Place("AutoUpdate", _autoUpdate));
        page.Children.Add(Hint($"New versions install while {Csi.Name} stays open and switch over when no job is running."));
        var tests = Check("Get test builds before they are released to everyone (only on the PC that runs the field test)");
        tests.IsChecked = _settings.TestBuilds;
        tests.Click += (_, _) => _settings.TestBuilds = tests.IsChecked == true;
        page.Children.Add(tests);
        page.Children.Add(Hint("With this on, each test build installs itself here the same way updates do, with nothing to paste. Press Save to keep it."));
        page.Children.Add(SubHeading("Background work"));
        page.Children.Add(Brand.Text($"CSI ProMax does nothing in {Csi.Name} until you open it from Tools > CSI ProMax, and it never asks a program anything while that program is busy with a run or a design."));
        var packs = Check("Write the review pack beside each model by itself after every analysis and design");
        packs.IsChecked = !CsiProMax.Assistant.Packs.PackWatcher.Off;
        packs.Click += (_, _) =>
        {
            try { CsiProMax.Assistant.Packs.PackWatcher.SetAutomatic(packs.IsChecked == true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { packs.IsChecked = !CsiProMax.Assistant.Packs.PackWatcher.Off; }
        };
        page.Children.Add(packs);
        page.Children.Add(Hint("Off by default: after every run it reads every result table, which keeps a large model busy for a long time. It takes effect at once, with nothing to restart. Off, a pack is still made whenever you ask for one in the chat."));
        page.Children.Add(SubHeading("This version"));
        string state;
        try { state = Update.Updater.Describe(); }
        catch (Exception) { state = "CSI ProMax " + Update.Updater.CurrentText + "."; }
        page.Children.Add(Brand.Text(state));
        var check = Brand.Button("Check for updates now");
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetHelpText(check, "Looks for a newer version now; the update window shows what it finds. It doesn't save the settings here.");
        check.Click += (_, _) => App.CheckForUpdatesInteractive();
        page.Children.Add(check);
        return page;
    }

    // ---- Tabs, keys and size ---------------------------------------------------------------------------------------

    /// <summary>The tab list on the left in palette colours (the stock tab look paints fixed light colours, wrong in the dark theme).</summary>
    private static void TabStyles(TabControl tabs)
    {
        const string xaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style x:Key="SpmSettingsTabs" TargetType="TabControl">
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0"/>
                <Setter Property="TabStripPlacement" Value="Left"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabControl">
                    <Grid KeyboardNavigation.TabNavigation="Local">
                      <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border Background="{DynamicResource SpmSurface}" BorderBrush="{DynamicResource SpmBorder}" BorderThickness="0,0,1,0">
                        <StackPanel IsItemsHost="True" MinWidth="170" Margin="0,10,0,10" KeyboardNavigation.TabIndex="1"
                                    KeyboardNavigation.TabNavigation="Once" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                      </Border>
                      <ContentPresenter Grid.Column="1" x:Name="PART_SelectedContentHost" ContentSource="SelectedContent"
                                        KeyboardNavigation.TabIndex="2" KeyboardNavigation.TabNavigation="Local"/>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{DynamicResource SpmText}"/>
                <Setter Property="FocusVisualStyle" Value="{DynamicResource SpmFocusVisual}"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="MinHeight" Value="34"/>
                <Setter Property="Template"><Setter.Value>
                  <ControlTemplate TargetType="TabItem">
                    <Grid Background="Transparent" SnapsToDevicePixels="True">
                      <Border x:Name="bg" Background="Transparent" Margin="6,1,8,1" CornerRadius="4"/>
                      <Rectangle x:Name="bar" Width="3" HorizontalAlignment="Left" Margin="6,7,0,7" RadiusX="1.5" RadiusY="1.5"
                                 Fill="{DynamicResource SpmAccent}" Visibility="Collapsed"/>
                      <ContentPresenter ContentSource="Header" Margin="18,8,16,8" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bg" Property="Background" Value="{DynamicResource SpmSurfaceHover}"/></Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="bg" Property="Background" Value="{DynamicResource SpmAccentSoft}"/>
                        <Setter TargetName="bar" Property="Visibility" Value="Visible"/>
                        <Setter Property="FontWeight" Value="SemiBold"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value></Setter>
              </Style>
            </ResourceDictionary>
            """;
        // A styling problem never stops Settings from opening: it falls back to the stock tabs (still on the left).
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(xaml);
            tabs.Resources.MergedDictionaries.Add(dictionary);
            tabs.Style = (Style)dictionary["SpmSettingsTabs"];
        }
        catch (Exception) { tabs.TabStripPlacement = Dock.Left; }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab (and Ctrl+Page Down / Up) switch tabs from anywhere in the window; Ctrl+Enter saves
    /// from the standards box, where Enter is a new line. Enter elsewhere is Save (IsDefault), Esc is Cancel (IsCancel).
    /// </summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key is Key.Tab or Key.PageDown or Key.PageUp)
        {
            var back = e.Key == Key.PageUp || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            _tabs.SelectedIndex = SettingsLayout.Next(Math.Max(0, _tabs.SelectedIndex), back);
            (_tabs.SelectedItem as TabItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is TextBox { AcceptsReturn: true })
        {
            Save();
            e.Handled = true;
        }
    }

    /// <summary>Focus on opening: the picked approval level on the Approvals tab, else the tab itself.</summary>
    private void FocusPage()
    {
        if ((_tabs.SelectedItem as TabItem)?.Tag as string == SettingsLayout.Approvals
            && _levels.FirstOrDefault(l => l.Button.IsChecked == true).Button is { } picked)
            picked.Focus();
        else (_tabs.SelectedItem as TabItem)?.Focus();
    }

    /// <summary>Shows the tab holding <paramref name="control"/> and focuses it once its page is on screen.</summary>
    private void ShowOnTab(string tab, UIElement control)
    {
        if (_tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag as string == tab) is { } item) _tabs.SelectedItem = item;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => control.Focus()));
    }

    private static string LastTabFile => Path.Combine(Path.GetDirectoryName(AgentSettings.FilePath)!, "settings-tab.txt");

    private static string? ReadLastTab()
    {
        try { return File.Exists(LastTabFile) ? File.ReadAllText(LastTabFile) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static void WriteLastTab(string? key)
    {
        if (key is null) return;
        try { SafeFile.Write(LastTabFile, key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* opens on the first tab next time */ }
    }

    /// <summary>
    /// The work area (taskbar left out) of the monitor ETABS is on, in device-independent pixels at that monitor's
    /// scale; (0, 0) when it can't be read (<see cref="SettingsLayout.Fit"/> then assumes a small laptop screen).
    /// </summary>
    internal static (double Width, double Height) ETABSWorkArea()
    {
        try
        {
            var etabs = App.MainWindow;
            if (etabs == IntPtr.Zero) return (0, 0);
            var monitor = MonitorFromWindow(etabs, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return (0, 0);
            uint dpi;
            try { dpi = GetDpiForWindow(etabs); }
            catch (EntryPointNotFoundException) { dpi = 0; } // before Windows 10 1607
            return SettingsLayout.ToDips(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, dpi > 0 ? dpi / 96.0 : 1);
        }
        catch (Exception) { return (0, 0); }
    }

    /// <summary>Moves the window fully into the work area of ETABS's monitor when centring on ETABS left part of it outside.</summary>
    private void KeepOnScreen()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            var monitor = MonitorFromWindow(App.MainWindow != IntPtr.Zero ? App.MainWindow : hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            var x = Math.Max(info.Work.Left, Math.Min(rect.Left, info.Work.Right - w));
            var y = Math.Max(info.Work.Top, Math.Min(rect.Top, info.Work.Bottom - h));
            if (x != rect.Left || y != rect.Top) SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception) { /* stays where it opened */ }
    }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Win32Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- Saving --------------------------------------------------------------------------------------------------

    private void Save()
    {
        _settings.Approvals = _levels.FirstOrDefault(l => l.Button.IsChecked == true).Level;
        _settings.NoAskTools = _noAsk.Count > 0 ? _noAsk : null;
        _settings.AutoUpdate = _autoUpdate.IsChecked == true;
        _settings.KeepProgramOpenAfterRun = _keepOpen.IsChecked == true;
        _settings.FirmStandards = _standards.Text;
        if (!int.TryParse(_syncChanges.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var syncChanges)
            || syncChanges < ETABS.AutoSyncRule.MinLimit || syncChanges > ETABS.AutoSyncRule.MaxLimit)
        {
            _error.Text = $"Type the number of changes before each sync (Chat tab) as a whole number from {ETABS.AutoSyncRule.MinLimit} to {ETABS.AutoSyncRule.MaxLimit} ({ETABS.AutoSyncRule.DefaultLimit} is usual), then press Save again.";
            _error.Visibility = Visibility.Visible;
            ShowOnTab(SettingsLayout.Chat, _syncChanges);
            return;
        }
        _settings.AutoSync = _autoSync.IsChecked == true;
        _settings.AutopilotResumeWeekly = _autopilotWeekly.IsChecked == true;
        _settings.AutopilotJobBudget = Picked(_apJob, Chat.JobBudget.DefaultPercent);
        _settings.AutopilotFiveHourMax = Picked(_apFive, 90);
        _settings.AutopilotWeekShare = Picked(_apWeek, 25);
        _settings.AutopilotAskWaitMinutes = Picked(_apAsk, Chat.AskRules.DefaultWaitMinutes);
        _settings.PhoneUpdateMinutes = Picked(_apUpdate, Chat.AskRules.DefaultUpdateMinutes);
        _settings.AutoSyncChanges = syncChanges;
        var motion = _motion.FirstOrDefault(m => m.Button.IsChecked == true).Level;
        _settings.ChatAnimations = Chat.MotionLevels.Store(motion);
        _settings.ChatJobLimit = (_jobLimit.SelectedItem as ComboBoxItem)?.Tag is int limit ? limit : Chat.JobBoard.DefaultLimit;
        _settings.ChatJobBudget = (_jobBudget.SelectedItem as ComboBoxItem)?.Tag is int budget ? budget : Chat.JobBudget.DefaultPercent;
        _settings.ChatJobReview = (_jobReview.SelectedItem as ComboBoxItem)?.Tag as string ?? Chat.JobReview.Always;
        _settings.ChatCrewYou = _crewYou.Text.Trim();
        // A box left on its default is stored blank, so a later default reaches it (26 Nov 2026: Atlas, Nova, Echo and Pixel
        // were stored as if typed by every Save, and the new names never showed).
        _settings.ChatCrewCoordinator = Chat.CrewNames.Stored(_crewCoordinator.Text, Chat.CrewNames.DefaultCoordinator, Chat.CrewNames.OldCoordinator);
        _settings.ChatCrewWorkers = Chat.CrewNames.Stored(_crewWorkers.Text, Chat.CrewNames.DefaultWorkers, Chat.CrewNames.OldWorkers);
        _settings.ChatCrewApplier = Chat.CrewNames.Stored(_crewApplier.Text, Chat.CrewNames.DefaultApplier);
        _settings.ChatTypeDefault = _chatTypes.FirstOrDefault(t => t.Button.IsChecked == true).Type ?? Chat.ChatTypes.Simple;
        _settings.ChatTypeChosen = true;
        _settings.ChatModelDefault = Picked("ChatModelDefault", Chat.ChatAuto.Automatic);
        _settings.ChatEffortDefault = Picked("ChatEffortDefault", Chat.ChatAuto.Automatic);
        _settings.ChatModelSimple = Picked("ChatModelSimple", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Simple));
        _settings.ChatModelEveryday = Picked("ChatModelEveryday", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Everyday));
        _settings.ChatModelHard = Picked("ChatModelHard", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Hard));
        _settings.ChatEffortSimple = Picked("ChatEffortSimple", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Simple));
        _settings.ChatEffortEveryday = Picked("ChatEffortEveryday", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Everyday));
        _settings.ChatEffortHard = Picked("ChatEffortHard", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Hard));
        _settings.ChatModelCoordinator = Picked("ChatModelCoordinator", Chat.ChatAuto.DefaultModel(Chat.ChatAuto.Coordinating));
        _settings.ChatEffortCoordinator = Picked("ChatEffortCoordinator", Chat.ChatAuto.DefaultEffort(Chat.ChatAuto.Coordinating));
        _settings.PhoneProjectNames = _phoneNames.Text.Trim();
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _error.Text = $"Couldn't save the settings: {ex.Message}\nClose any program that has the settings file open " +
                          $"(or check you can write to {AgentSettings.FilePath}), then press Save again. Nothing was changed.";
            _error.Visibility = Visibility.Visible;
            return;
        }
        Bridge.Approvals.RaiseLevelChanged();
        Chat.ChatUi.SetMotion(motion);
        Chat.CrewStrip.ReloadNames();
        DialogResult = true;
    }
}
