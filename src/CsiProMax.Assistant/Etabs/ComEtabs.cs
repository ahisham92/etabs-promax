using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// The running ETABS, through ETABS's own API (the same one ETABS plug-ins and spreadsheets use). ETABS must be open
/// and licensed: this attaches to it and never starts, unlocks or stands in for it. Calls are made by name at run
/// time through <see cref="EtabsApi"/>, so nothing of ETABS is needed to build. Every call runs on one thread of its
/// own, one at a time.
/// </summary>
public sealed class ComEtabs : ICsiLink, IDisposable
{
    /// <summary>
    /// The COM apartment of the thread that talks to the program. --mta on the command line makes it multi-threaded,
    /// as CSI's own console examples are: a way to try, on a real program, whether a call that never comes back
    /// (SAP2000's RunAnalysis in the first full field test) is the apartment's doing.
    /// </summary>
    public static ApartmentState Apartment { get; set; } = ApartmentState.STA;

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private dynamic? _model;

    private readonly int? _pid;

    public CsiProgram Program { get; }

    /// <param name="pid">The ETABS window to work on, by its process id. Left out: the one ETABS marks as active for the API.</param>
    /// <param name="program">Which CSI program this link drives. Left out: ETABS.</param>
    public ComEtabs(int? pid = null, CsiProgram? program = null)
    {
        _pid = pid;
        Program = program ?? CsiProgram.Etabs;
        _thread = new Thread(() => { foreach (var job in _work.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = Program.ProcessName };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(Apartment);
        _thread.Start();
    }

    private readonly Func<dynamic>? _start;
    private bool _started;

    /// <summary>
    /// A link to a window of the program that this link starts for itself (the field test's scratch window): the
    /// model comes from <paramref name="start"/>, called once on this link's own thread. It never attaches to a
    /// window the engineer has open, and when its window is gone it says so instead of looking for another.
    /// </summary>
    internal ComEtabs(CsiProgram program, Func<dynamic> start) : this(null, program)
    {
        _start = start;
        // The scratch model is small: its analysis takes seconds, so a long wait means a window is waiting.
        AnalysisLimit = TimeSpan.FromMinutes(10);
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        try { _pipe?.Dispose(); } catch (Exception) { /* the program has gone already */ }
    }

    /// <summary>
    /// How long an ordinary call (a read, a table, a definition, a change) may take before the link stops waiting
    /// for it. Reading a large table takes seconds to a minute; a call still out after this is waiting on something.
    /// </summary>
    public TimeSpan CallLimit { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long an analysis, a design, or opening or saving a model may take (the calls whose purpose starts with
    /// "run ", "start ", "open ", "save " or "import "). A large
    /// model's analysis can take hours, so the engineer's own window gets hours; a scratch window's model is small.
    /// </summary>
    public TimeSpan AnalysisLimit { get; set; } = TimeSpan.FromHours(4);

    /// <summary>The call the program has not come back from, while it is still out ("" when none).</summary>
    private volatile string _stuck = "";

    internal static bool IsLong(string what) =>
        new[] { "run ", "start ", "open ", "save ", "import " }.Any(w => what.StartsWith(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs one piece of work on the program's thread and waits for it, for at most <see cref="CallLimit"/>
    /// (<see cref="AnalysisLimit"/> for an analysis or design). A call the program does not come back from (a message
    /// waiting in its window, such as "Analysis Complete") is given up on with a message that says so, and every
    /// later call says the same at once instead of waiting behind it, until the program answers that call.
    /// </summary>
    private T On<T>(string what, Func<dynamic, T> body)
    {
        var app = Program.Name;
        if (_stuck is { Length: > 0 } stuck)
            throw new EtabsException($"{app} has still not come back from the last call ({stuck}). A message may be waiting in {app}'s window " +
                "(such as \"Analysis Complete\" or a licence notice): answer or close it there, then try again.");
        // A call made from inside another call (a tool's Raw work that asks the link for the load cases) is already on
        // the program's thread: it runs at once. Queued, it would wait for the call it is part of, which never ends.
        if (Thread.CurrentThread == _thread) return body(Model());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(body(Model())); }
            catch (EtabsException ex) { done.SetException(ex); }
            catch (COMException ex)
            {
                // ETABS was closed, or is closing: attach again on the next call.
                _model = null;
                done.SetException(new EtabsException($"{app} did not answer ({what}). Is {app} still open? {ex.Message}"));
            }
            catch (Exception ex) { done.SetException(new EtabsException($"{app} could not {what}: {ex.Message}")); }
            finally { _stuck = ""; }
        });
        var limit = IsLong(what) ? AnalysisLimit : CallLimit;
        // Waits without throwing: a call that failed must come out as its own EtabsException below, never wrapped
        // in an AggregateException that no "catch (EtabsException)" would take.
        if (!((IAsyncResult)done.Task).AsyncWaitHandle.WaitOne(limit))
        {
            _stuck = Volatile.Read(ref _refreshing) == 1 ? "refresh the view" : what;
            // The call may still come back later; the next call works again from then on.
            throw new EtabsException($"{app} did not come back from \"{what}\" within {Span(limit)}. A message may be waiting in {app}'s window " +
                "(such as \"Analysis Complete\" or a licence notice): answer or close it there, then try again.");
        }
        try { return done.Task.GetAwaiter().GetResult(); }
        catch (EtabsException) { throw; }
    }

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours" : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalSeconds:0} seconds";

    /// <summary>The same for work that gives nothing back. Its own name, so a call can never be mistaken for one that does.</summary>
    public object? Raw(string what, Func<dynamic, object?> work) => On<object?>(what, m => work(m));

    private void Do(string what, Action<dynamic> body)
    {
        Forget();
        On<bool>(what, m => { body(m); return true; });
    }

    private dynamic Model()
    {
        if (_model is not null) return _model;
        var app = Program.Name;
        if (_start is not null)
        {
            if (_started) throw new EtabsException($"The scratch {app} window is no longer there.");
            _started = true;
            _model = _start();
            return _model!;
        }
        if (!OperatingSystem.IsWindows()) throw new EtabsException($"{app} runs on Windows only.");
        var apiFile = EtabsApi.FindApiFile(Program, _pid);
        if (!File.Exists(apiFile))
            throw new EtabsException($"{app} is not installed on this PC, or its API is not registered. Open {app} once as administrator to register it.");
        // A program on the .NET Framework (ETABS 21, SAP2000 25, SAFE 21 and older): its API file reaches it through
        // .NET Remoting, which this .NET 8 window does not have. Its Helper answered nothing on SAFE 20, ETABS 20 and
        // SAP2000 24 (tester's field test, 2026-11-26), so every read said the window was closed. The calls go through
        // the plug-in's pipe instead.
        if (EtabsApi.RunsOnFramework(apiFile)) return _model = ThroughPipe(apiFile);
        // Plain COM late binding fails on ETABS 23 ("Specified cast is not valid" for SapModel), so the calls go
        // through the API's own interfaces in ETABSv1.dll, still by name.
        dynamic? etabs = EtabsApi.Attach(apiFile, Program, _pid);
        // The plug-in's pipe as well, when the API did not find the window the plug-in was started from.
        if (etabs is null && _pid is int pid && Running(pid) && ApiPipe.ToPlugin(pid, app) is { } plugin) return _model = Keep(plugin);
        if (etabs is null)
            throw new EtabsException(_pid is int gone && !Running(gone) ? $"The {app} window this was started from is closed. Start CSI ProMax again from {app}'s Tools menu."
                : _pid is null ? $"{app} is not open. Open {app} and a model, then try again."
                : $"{app} is open, but its API did not answer CSI ProMax. Close this window and start CSI ProMax again from {app}'s Tools menu.");
        _model = etabs.SapModel;
        return _model!;
    }

    /// <summary>The pipe to a .NET Framework program this link calls through (null: the program's API is called here).</summary>
    private ApiPipe? _pipe;

    /// <summary>How this link reaches the program, for the report: "the program's API", or the pipe's host.</summary>
    public string Via => _pipe?.Via ?? "the program's API";

    private dynamic Keep(ApiPipe pipe)
    {
        _pipe?.Dispose();
        _pipe = pipe;
        dynamic root = pipe.Root();
        return pipe.RootType.Name == "cOAPI" ? root.SapModel : root;
    }

    /// <summary>
    /// The model of a .NET Framework program: through the plug-in in the window this was started from, else through
    /// Windows PowerShell (the program's running window).
    /// </summary>
    private dynamic ThroughPipe(string apiFile)
    {
        var app = Program.Name;
        if (_pid is int pid)
        {
            if (!Running(pid)) throw new EtabsException($"The {app} window this was started from is closed. Start CSI ProMax again from {app}'s Tools menu.");
            if (ApiPipe.ToPlugin(pid, app) is { } plugin) return Keep(plugin);
        }
        else if (Process.GetProcessesByName(Program.ProcessName).Length == 0)
            throw new EtabsException($"{app} is not open. Open {app} and a model, then try again.");
        try { return Keep(ApiPipe.ToPowerShell(Program, apiFile, "attach", _pid)); }
        catch (Exception ex) when (ex is EtabsException or System.Reflection.TargetInvocationException or COMException)
        {
            var why = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            throw new EtabsException($"{app} on this PC runs on the .NET Framework, and CSI ProMax reaches it through its plug-in. Close this window and start CSI ProMax " +
                $"from {app}'s Tools menu (if CSI ProMax is not in that menu, close {app} and run the install line again). ({why})");
        }
    }

    /// <summary>The process of the window this link started for itself (the scratch window), once it is known.</summary>
    internal int? Known { get; set; }

    /// <summary>The program's process: the window this link was started from, else the only one open (null: none or several).</summary>
    public int? ProcessId
    {
        get
        {
            if ((_pid ?? Known) is int pid) return Running(pid) ? pid : null;
            var all = Process.GetProcessesByName(Program.ProcessName);
            try { return all.Length == 1 ? all[0].Id : null; }
            finally { foreach (var p in all) p.Dispose(); }
        }
    }

    private static bool Running(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The API answers 0 when a call worked. The refusal names the program this link drives, never another.</summary>
    private void Ok(object result, string what)
    {
        if (Convert.ToInt32(result) != 0) throw new EtabsException($"{Program.Name} refused to {what}.");
    }

    private static string[] None => [];
    private static double[] NoNumbers => [];

    public ModelInfo Info() => On("read the model", m =>
    {
        string version = ""; double number = 0;
        try { m.GetVersion(ref version, ref number); } catch (Exception ex) when (ex is not COMException) { }
        int force = 0, length = 0, temperature = 0;
        try { m.GetPresentUnits_2(ref force, ref length, ref temperature); } catch (Exception ex) when (ex is not COMException) { }
        string file = Convert.ToString(m.GetModelFilename(true)) ?? "";
        bool locked = Convert.ToBoolean(m.GetModelIsLocked());
        var (f, l, t) = (Name(Forces, force), Name(Lengths, length), Name(Temperatures, temperature));
        // SAP2000 26 answered GetPresentUnits_2 with nothing on Ahmed's PC: every program also has the older
        // GetPresentUnits, one value naming all three (kN_m_C).
        if (f.Length == 0 || l.Length == 0)
            try { object present = m.GetPresentUnits(); (f, l, t) = Units(present); } catch (Exception ex) when (ex is not COMException) { }
        return new ModelInfo(file, version, f, l, t, locked);
    });

    /// <summary>The CSI eUnits value, by its name (kN_m_C) or its number (6), as force, length and temperature.</summary>
    internal static (string Force, string Length, string Temperature) Units(object? units)
    {
        var text = Convert.ToString(units, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (int.TryParse(text, out var number)) text = number >= 1 && number <= UnitNames.Length ? UnitNames[number - 1] : "";
        var parts = text.Split('_');
        if (parts.Length != 3) return ("", "", "");
        var force = parts[0].Equals("Ton", StringComparison.OrdinalIgnoreCase) ? "tonf" : parts[0];
        return (force, parts[1], parts[2]);
    }

    internal static readonly string[] UnitNames =
    [
        "lb_in_F", "lb_ft_F", "kip_in_F", "kip_ft_F", "kN_mm_C", "kN_m_C", "kgf_mm_C", "kgf_m_C",
        "N_mm_C", "N_m_C", "Ton_mm_C", "Ton_m_C", "kN_cm_C", "kgf_cm_C", "N_cm_C", "Ton_cm_C",
    ];

    private static readonly string[] Forces = ["", "lb", "kip", "N", "kN", "kgf", "tonf"];
    private static readonly string[] Lengths = ["", "in", "ft", "micron", "mm", "cm", "m"];
    private static readonly string[] Temperatures = ["", "F", "C"];
    private static string Name(string[] names, int value) => value > 0 && value < names.Length ? names[value] : "";

    public IReadOnlyList<StoryRow> Stories() => On("read the stories", m =>
    {
        double baseElevation = 0; int n = 0;
        string[] names = None, similar = None; double[] elevations = NoNumbers, heights = NoNumbers, spliceHeight = NoNumbers;
        bool[] master = [], splice = []; int[] color = [];
        Ok(m.Story.GetStories_2(ref baseElevation, ref n, ref names, ref elevations, ref heights, ref master, ref similar, ref splice, ref spliceHeight, ref color), "list the stories");
        var rows = new List<StoryRow> { new("Base", baseElevation, 0) };
        for (var i = 0; i < n; i++) rows.Add(new StoryRow(names[i], elevations[i], heights[i]));
        return (IReadOnlyList<StoryRow>)rows.OrderBy(r => r.Elevation).ToList();
    });

    public IReadOnlyList<FrameRow> Frames() => On("read the frames", m =>
        // A null line (section "None", which the API answers as "") is no frame: ETABS 23 listed the Modeler's slab
        // line release line CPM-LR as frame 72 with no section (0.4.59 full field test).
        (IReadOnlyList<FrameRow>)((IReadOnlyList<FrameRow>)AllFrames(m)).Where(r => !IsNullLine(r.Section)).ToList());

    /// <summary>Every line object the program has, null lines too (their section ""): FrameObj.GetAllFrames.</summary>
    internal static IReadOnlyList<FrameRow> AllFrames(dynamic m)
    {
        int n = 0; string[] names = None, sections = None, stories = None, p1 = None, p2 = None;
        double[] x1 = NoNumbers, y1 = NoNumbers, z1 = NoNumbers, x2 = NoNumbers, y2 = NoNumbers, z2 = NoNumbers, angle = NoNumbers;
        double[] o1x = NoNumbers, o2x = NoNumbers, o1y = NoNumbers, o2y = NoNumbers, o1z = NoNumbers, o2z = NoNumbers; int[] cardinal = [];
        int answer = Convert.ToInt32(m.FrameObj.GetAllFrames(ref n, ref names, ref sections, ref stories, ref p1, ref p2, ref x1, ref y1, ref z1, ref x2, ref y2, ref z2,
            ref angle, ref o1x, ref o2x, ref o1y, ref o2y, ref o1z, ref o2z, ref cardinal, "Global"));
        if (answer != 0) throw new EtabsException("The program refused to list the frames.");
        var rows = new List<FrameRow>(n);
        for (var i = 0; i < n; i++)
            rows.Add(new FrameRow(names[i], sections[i] ?? "", stories[i] ?? "", EtabsWords.FrameKind(x1[i], y1[i], z1[i], x2[i], y2[i], z2[i]), x1[i], y1[i], z1[i], x2[i], y2[i], z2[i]));
        return rows;
    }

    public IReadOnlyList<AreaRow> Areas() => On("read the areas", m =>
    {
        int n = 0; string[] names = None;
        Ok(m.AreaObj.GetNameList(ref n, ref names), "list the areas");
        var points = new Dictionary<string, (double, double, double)>(StringComparer.Ordinal);
        {
            int count = 0; string[] pn = None; double[] x = NoNumbers, y = NoNumbers, z = NoNumbers;
            Ok(m.PointObj.GetAllPoints(ref count, ref pn, ref x, ref y, ref z, "Global"), "list the points");
            for (var i = 0; i < count; i++) points[pn[i]] = (x[i], y[i], z[i]);
        }
        var rows = new List<AreaRow>(n);
        for (var i = 0; i < n; i++)
        {
            int count = 0; string[] corners = None; string section = "";
            // An opening is an area object to the API (section "None"), not a floor or a wall: the Modeler keeps its
            // openings apart, and ETABS 23 listed the Modeler's CPM-OPEN as a 15th area (0.4.57 field test).
            if (IsOpening(m, names[i])) continue;
            Ok(m.AreaObj.GetPoints(names[i], ref count, ref corners), "read an area's corners");
            m.AreaObj.GetProperty(names[i], ref section);
            var at = corners.Take(count).Select(c => points.TryGetValue(c, out var p) ? p : (0, 0, 0)).ToList();
            var level = at.Count > 0 && at.All(p => Math.Abs(p.Item3 - at[0].Item3) < 1e-6);
            rows.Add(new AreaRow(names[i], section, level ? "floor" : "wall", at));
        }
        return (IReadOnlyList<AreaRow>)rows;
    });

    /// <summary>A line object with no section: ETABS's null line ("None", answered as "" by GetAllFrames).</summary>
    internal static bool IsNullLine(string? section) => string.IsNullOrWhiteSpace(section) || section.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the program marks the area an opening (AreaObj.GetOpening); a program without the call has none.</summary>
    private static bool IsOpening(dynamic m, string name)
    {
        try { bool open = false; return Convert.ToInt32(m.AreaObj.GetOpening(name, ref open)) == 0 && open; }
        catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException or ArgumentException) { return false; }
    }

    public IReadOnlyList<PointRow> Points() => On("read the points", m =>
    {
        int n = 0; string[] names = None; double[] x = NoNumbers, y = NoNumbers, z = NoNumbers;
        Ok(m.PointObj.GetAllPoints(ref n, ref names, ref x, ref y, ref z, "Global"), "list the points");
        return (IReadOnlyList<PointRow>)Enumerable.Range(0, n).Select(i => new PointRow(names[i], x[i], y[i], z[i])).ToList();
    });

    public bool[] Restraint(string point) => On("read a support", m =>
    {
        bool[] value = new bool[6];
        Ok(m.PointObj.GetRestraint(point, ref value), $"read the support of point {point}");
        return value;
    });

    public IReadOnlyList<(string Kind, string Name)> Selected() => On("read the selection", m =>
    {
        int n = 0; int[] types = []; string[] names = None;
        Ok(m.SelectObj.GetSelected(ref n, ref types, ref names), "read the selection");
        return (IReadOnlyList<(string, string)>)Enumerable.Range(0, n)
            .Select(i => (types[i] switch { 1 => "point", 2 => "frame", 5 => "area", 7 => "link", _ => "other" }, names[i])).ToList();
    });

    public void ClearSelection() => Do("clear the selection", m => { Ok(m.SelectObj.ClearSelection(), "clear the selection"); });

    public void Select(string kind, string name) => Do("select", m =>
    {
        Ok(kind switch
        {
            "frame" => m.FrameObj.SetSelected(name, true, 0),
            "area" => m.AreaObj.SetSelected(name, true, 0),
            "point" => m.PointObj.SetSelected(name, true, 0),
            _ => throw new EtabsException($"'{kind}' can't be selected: use frame, area or point."),
        }, $"select {kind} {name}");
    });

    private IReadOnlyList<string> Names(string what, Func<dynamic, object> list) => On($"list the {what}", m =>
    {
        var names = (string[])list(m);
        return (IReadOnlyList<string>)names.ToList();
    });

    public IReadOnlyList<string> FrameSections() => Names("frame sections", m => { int n = 0; string[] a = None; Ok(m.PropFrame.GetNameList(ref n, ref a), "list the frame sections"); return (a ?? None).Take(n).ToArray(); });
    public IReadOnlyList<string> AreaSections() => Names("slab and wall sections", m => { int n = 0; string[] a = None; Ok(m.PropArea.GetNameList(ref n, ref a), "list the slab and wall sections"); return (a ?? None).Take(n).ToArray(); });
    public IReadOnlyList<string> Materials() => Names("materials", m => { int n = 0; string[] a = None; Ok(m.PropMaterial.GetNameList(ref n, ref a), "list the materials"); return (a ?? None).Take(n).ToArray(); });
    public IReadOnlyList<string> LoadCases() => Names("load cases", m => { int n = 0; string[] a = None; Ok(m.LoadCases.GetNameList(ref n, ref a), "list the load cases"); return (a ?? None).Take(n).ToArray(); });
    public IReadOnlyList<string> Combos() => Names("load combinations", m => { int n = 0; string[] a = None; Ok(m.RespCombo.GetNameList(ref n, ref a), "list the load combinations"); return (a ?? None).Take(n).ToArray(); });

    public IReadOnlyList<LoadPatternRow> LoadPatterns() => On("read the load patterns", m =>
    {
        int n = 0; string[] names = None;
        Ok(m.LoadPatterns.GetNameList(ref n, ref names), "list the load patterns");
        var rows = new List<LoadPatternRow>(n);
        for (var i = 0; i < n; i++)
        {
            int type = 0; double self = 0;
            try { m.LoadPatterns.GetLoadType(names[i], ref type); m.LoadPatterns.GetSelfWTMultiplier(names[i], ref self); }
            catch (Exception ex) when (ex is not COMException) { }
            var name = EtabsWords.PatternTypes.FirstOrDefault(t => t.Value == type).Name ?? "Other";
            rows.Add(new LoadPatternRow(names[i], name, self));
        }
        return (IReadOnlyList<LoadPatternRow>)rows;
    });

    public string AddFrame(double x1, double y1, double z1, double x2, double y2, double z2, string section) => On("add a frame", m =>
    {
        string name = "";
        Ok(m.FrameObj.AddByCoord(x1, y1, z1, x2, y2, z2, ref name, section, "", "Global"), $"add a frame with section {section}");
        return name;
    });

    public string AddArea(IReadOnlyList<(double X, double Y, double Z)> corners, string section) => On("add an area", m =>
    {
        double[] x = corners.Select(c => c.X).ToArray(), y = corners.Select(c => c.Y).ToArray(), z = corners.Select(c => c.Z).ToArray();
        string name = "";
        Ok(m.AreaObj.AddByCoord(corners.Count, ref x, ref y, ref z, ref name, section, "", "Global"), $"add an area with section {section}");
        return name;
    });

    // ETABS 23 answers SetSection for a frame that is not there with 0 (done), as if it had worked (0.4.9 field test,
    // Ahmed's PC): the section is read back, so a missing object or a change the program did not make is reported.
    public void SetFrameSection(string name, string section) => Do("change a section", m =>
    {
        Ok(m.FrameObj.SetSection(name, section, 0, 0.0, 0.0), $"give frame {name} the section {section}");
        string now = "", auto = "";
        try { m.FrameObj.GetSection(name, ref now, ref auto); } catch (Exception ex) when (ex is not COMException) { now = section; }
        Given("frame", name, section, now);
    });
    public void SetAreaSection(string name, string section) => Do("change a section", m =>
    {
        Ok(m.AreaObj.SetProperty(name, section, 0), $"give area {name} the section {section}");
        string now = "";
        try { m.AreaObj.GetProperty(name, ref now); } catch (Exception ex) when (ex is not COMException) { now = section; }
        Given("area", name, section, now);
    });

    /// <summary>Throws when the section read back is not the one given: the object is not there, or the program kept its old section.</summary>
    internal void Given(string kind, string name, string section, string? now)
    {
        if (string.Equals(now ?? "", section, StringComparison.OrdinalIgnoreCase)) return;
        throw new EtabsException(string.IsNullOrEmpty(now)
            ? $"{Program.Name} has no {kind} named {name}, so it was not given {section}."
            : $"{Program.Name} kept {kind} {name} on {now}, not {section}.");
    }

    public void Delete(string kind, string name) => Do("delete", m =>
    {
        Ok(kind switch
        {
            "frame" => m.FrameObj.Delete(name, 0),
            "area" => m.AreaObj.Delete(name, 0),
            // A joint nothing is drawn to: the program keeps it as a "special point" and lets only such a one go.
            // (delete_joints left its test joints in the model on ETABS 23, 2026-11-25: this kind was not known here.)
            "point" => m.PointObj.DeleteSpecialPoint(name, 0),
            _ => throw new EtabsException($"'{kind}' can't be deleted here: use frame, area or point."),
        }, kind == "point" ? $"delete joint {name} (a joint that frames or areas use cannot be deleted)" : $"delete {kind} {name}");
    });

    public void SetRestraint(string point, bool[] restraint) => Do("set a support", m =>
    {
        bool[] value = restraint.ToArray();
        Ok(m.PointObj.SetRestraint(point, ref value, 0), $"set the support of point {point}");
    });

    public void AddLoadPattern(string name, string type, double selfWeight) => Do("add a load pattern", m =>
    {
        var value = EtabsWords.PatternTypes.FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)).Value;
        if (value == 0) throw new EtabsException($"'{type}' is not a load pattern type. Use one of: {string.Join(", ", EtabsWords.PatternTypes.Select(t => t.Name))}.");
        Ok(m.LoadPatterns.Add(name, value, selfWeight, true), $"add the load pattern {name}");
    });

    // Direction 10 is gravity; type 1 is a force per length.
    public void SetFrameLoad(string frame, string pattern, double value, bool replace) => Do("load a frame", m => { Ok(m.FrameObj.SetLoadDistributed(frame, pattern, 1, 10, 0.0, 1.0, value, value, "Global", true, replace, 0), $"put the load on frame {frame}"); });

    public void SetAreaLoad(string area, string pattern, double value, bool replace) => Do("load an area", m => { Ok(m.AreaObj.SetLoadUniform(area, pattern, value, 10, replace, "Global", 0), $"put the load on area {area}"); });

    public void AddCombo(string name, IReadOnlyList<(string Case, double Factor)> cases) => Do("add a combination", m =>
    {
        Ok(m.RespCombo.Add(name, 0), $"add the combination {name}");
        foreach (var (item, factor) in cases)
        {
            int kind = 0; // a load case
            Ok(m.RespCombo.SetCaseList(name, ref kind, item, factor), $"add {item} to the combination {name}");
        }
    });

    public IReadOnlyList<TableName> Tables()
    {
        if (_session is { Tables: { } kept }) return kept;
        var list = On("list the tables", m => WithStories((IReadOnlyList<TableName>)ListTables(m.DatabaseTables, Program.Name), Program));
        if (_session is { } s) s.Tables = list;
        return list;
    }

    /// <summary>
    /// What a read session (<see cref="PackSession"/>) has read already: the table list and each table, by its key,
    /// cases and combinations. Writing a pack lists the tables once (GetAllTables checks every one of ETABS's ~1,000
    /// tables for data, seconds each time) where it listed them again for every table the program then refused, and
    /// reads a table the checks read already only once.
    /// </summary>
    internal sealed class ReadCache
    {
        public IReadOnlyList<TableName>? Tables;
        /// <summary>The cases and combinations the display selection was set for in this session.</summary>
        public string? ShownFor;
        public readonly Dictionary<string, TableData> Reads = new(StringComparer.Ordinal);
        public int Depth = 1;
    }

    private volatile ReadCache? _session;

    /// <summary>Starts a read session (nested ones share the outer one). Every change through this link ends what it kept.</summary>
    internal IDisposable StartSession()
    {
        lock (_work)
        {
            if (_session is { } s) s.Depth++;
            else _session = new ReadCache();
        }
        return new Ending(this);
    }

    /// <summary>Forgets what the session read (the program's output options changed), keeping the session.</summary>
    internal void Forget()
    {
        if (_session is { } s) lock (_work) { s.Tables = null; s.ShownFor = null; s.Reads.Clear(); }
    }

    private sealed class Ending(ComEtabs link) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (link._work)
                if (link._session is { } s && --s.Depth <= 0) link._session = null;
        }
    }

    /// <summary>ETABS's list with "Story Definitions" in it: ETABS 23.2 left it out on the scratch model before the
    /// analysis (0.4.59 to 0.4.61 full tests), and ReadTable gives it from the Story API then.</summary>
    internal static IReadOnlyList<TableName> WithStories(IReadOnlyList<TableName> list, CsiProgram program) =>
        program.Key != CsiProgram.Etabs.Key || list.Count == 0 || list.Any(t => string.Equals(t.Key, StoryTable, StringComparison.OrdinalIgnoreCase))
            ? list : [.. list, new TableName(StoryTable, StoryTable, false, -1)];

    /// <summary>
    /// The program's tables: GetAllTables, else GetAvailableTables (the tables with data). SAFE 23.1 refused
    /// GetAllTables on a new model not saved yet (tester's PC, 0.4.56, 2026-11-26) and listed all 286 once saved;
    /// the second call is the way round it, and both answers are named when neither works.
    /// </summary>
    internal static IReadOnlyList<TableName> ListTables(dynamic tables, string program)
    {
        int n = 0; string[] keys = None, names = None; int[] import = []; bool[] empty = [];
        int all = Convert.ToInt32(tables.GetAllTables(ref n, ref keys, ref names, ref import, ref empty));
        if (all != 0)
        {
            n = 0; keys = None; names = None; import = []; empty = [];
            int available;
            try { available = Convert.ToInt32(tables.GetAvailableTables(ref n, ref keys, ref names, ref import)); }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException) { available = -1; }
            if (available != 0) throw new EtabsException($"{program} refused to list the tables (GetAllTables answered {all}, GetAvailableTables {available}).");
        }
        keys ??= None; names ??= None; empty ??= []; import ??= [];
        // The arrays are what the program filled; a count below them is not trusted (ETABS 23 listed no table from
        // "Story Definitions" on in the 0.4.59 full test, although it reads it).
        n = keys.Length == names.Length ? keys.Length : Math.Min(n, Math.Min(keys.Length, names.Length));
        var list = Enumerable.Range(0, n).Select(i => new TableName(keys[i], names[i], i < empty.Length && empty[i], i < import.Length ? import[i] : -1)).ToList();
        // ETABS 23's GetAllTables left out every "Story ..." table (Story Definitions, Story Drifts) that it reads and
        // GetAvailableTables lists (0.4.59 and 0.4.60 full tests): the tables with data are added from there.
        if (all == 0)
            try
            {
                int an = 0; string[] ak = None, aname = None; int[] ai = [];
                if (Convert.ToInt32(tables.GetAvailableTables(ref an, ref ak, ref aname, ref ai)) == 0 && ak is not null && aname is not null)
                {
                    var have = list.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < Math.Min(ak.Length, aname.Length); i++)
                        if (have.Add(ak[i])) list.Add(new TableName(ak[i], aname[i], false, ai is not null && i < ai.Length ? ai[i] : -1));
                }
            }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException or COMException or InvalidCastException) { /* GetAllTables' list stands */ }
        return list;
    }

    /// <summary>ETABS's story table, by its key.</summary>
    internal const string StoryTable = "Story Definitions";

    /// <summary>ETABS's "Story Definitions" table (ETABS 23.2's fields), made from Story.GetStories_2: top story first, the base left out.</summary>
    internal static TableData StoryDefinitions(dynamic m)
    {
        double baseElevation = 0; int n = 0;
        string[] names = None, similar = None; double[] elevations = NoNumbers, heights = NoNumbers, spliceHeight = NoNumbers;
        bool[] master = [], splice = []; int[] color = [];
        if (Convert.ToInt32(m.Story.GetStories_2(ref baseElevation, ref n, ref names, ref elevations, ref heights, ref master, ref similar, ref splice, ref spliceHeight, ref color)) != 0)
            throw new EtabsException("ETABS refused to read the table 'Story Definitions' and to list the stories."); // ETABS itself
        names ??= None; n = Math.Min(n, names.Length);
        static string At<T>(T[]? list, int i, Func<T, string> text) => list is not null && i < list.Length ? text(list[i]) : "";
        var rows = new List<(double Z, string[] Row)>();
        for (var i = 0; i < n; i++)
        {
            var guid = "";
            try { string g = ""; if (Convert.ToInt32(m.Story.GetGUID(names[i], ref g)) == 0) guid = g ?? ""; }
            catch (Exception ex) when (ex is not COMException) { }
            var z = elevations is not null && i < elevations.Length ? elevations[i] : i;
            rows.Add((z, new[]
            {
                "T1", names[i], At(heights, i, N), At(master, i, b => b ? "Yes" : "No"), At(similar, i, s => s ?? ""), At(splice, i, b => b ? "Yes" : "No"),
                At(spliceHeight, i, N), At(color, i, c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)), guid,
            }));
        }
        return new TableData(["Tower", "Story", "Height", "IsMaster", "SimilarTo", "IsSpliced", "SpliceHt", "Color", "GUID"],
            rows.OrderByDescending(r => r.Z).Select(r => r.Row).ToList());
        static string N(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ModelFileOf(dynamic m)
    {
        try { return Convert.ToString(m.GetModelFilename(true)) ?? ""; }
        catch (Exception ex) when (ex is not COMException) { return ""; }
    }

    /// <summary>The cases, combinations, patterns and model the display selection was last set for (ReadTable).</summary>
    private string? _shown;

    public TableData ReadTable(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos)
    {
        if (_session is not { } session) return ReadTableNow(key, cases, combos);
        var id = key + "\u0002" + string.Join("\u0001", cases ?? []) + "\u0002" + string.Join("\u0001", combos ?? []);
        lock (_work) if (session.Reads.TryGetValue(id, out var kept)) return kept;
        var data = ReadTableNow(key, cases, combos);
        lock (_work) if (_session == session) session.Reads[id] = data;
        return data;
    }

    private TableData ReadTableNow(string key, IReadOnlyList<string>? cases, IReadOnlyList<string>? combos) => On("read a table", m =>
    {
        // The display selection is set only when it changes: set before every one of a pack's 230 tables, ETABS 23 took
        // 2.3 s a table and up to 11 minutes for one (0.4.60 full test, pack trace), and the ETABS part ran out of time.
        // In a read session the model does not change: once the selection is set for these cases and combinations, the
        // patterns and the model file are not asked for again before every table (GetModelFilename was never timed, and
        // 0.4.61's packs still had a 2.4 s floor a table where ETABS itself gives a small table in 0.2-0.5 s, 2026-11-27).
        var asked = string.Join("\u0001", cases ?? []) + "\u0002" + string.Join("\u0001", combos ?? []);
        if (_session is { } inSession && inSession.ShownFor == asked && _shown is not null) goto read;
        int patternCount = 0; string[] patterns = None;
        try { if (Convert.ToInt32(m.LoadPatterns.GetNameList(ref patternCount, ref patterns)) != 0) patterns = None; }
        catch (Exception ex) when (ex is not COMException) { patterns = None; }
        var shown = string.Join("\u0001", cases ?? []) + "\u0002" + string.Join("\u0001", combos ?? []) + "\u0002" + string.Join("\u0001", patterns ?? None) + "\u0002" + ModelFileOf(m);
        if (!string.Equals(shown, _shown, StringComparison.Ordinal))
        {
            if (cases is { Count: > 0 }) { string[] list = cases.ToArray(); m.DatabaseTables.SetLoadCasesSelectedForDisplay(ref list); }
            if (combos is { Count: > 0 }) { string[] list = combos.ToArray(); m.DatabaseTables.SetLoadCombinationsSelectedForDisplay(ref list); }
            // Load assignment and pattern tables list only the patterns selected for display: SAP2000 26 gave a pack none of
            // its frame, area or joint loads (0.4.51 field test, Display > Load Assigns greyed out). Every pattern, then.
            EditableTables.ShowEveryPattern(m);
            _shown = shown;
        }
        if (_session is { } setIn) setIn.ShownFor = asked;
        read:
        // Section cut forces are given only for the cuts selected for display; none is selected at first (ETABS 23
        // answered no rows for a cut that was defined and analysed), so every cut is selected. Best effort.
        if (key.Contains("Section Cut Forces", StringComparison.OrdinalIgnoreCase))
            try
            {
                int cutCount = 0; string[] cuts = None;
                if (Convert.ToInt32(m.SectCut.GetNameList(ref cutCount, ref cuts)) == 0 && cuts is { Length: > 0 }) m.DatabaseTables.SetSectionCutsSelectedForDisplay(ref cuts);
            }
            catch (Exception ex) when (ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or MissingMethodException or System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException) { }
        string[] ask = None, fields = None, data = None; int version = 0, records = 0;
        int ret = Convert.ToInt32(m.DatabaseTables.GetTableForDisplayArray(key, ref ask, "", ref version, ref fields, ref records, ref data));
        fields ??= None; data ??= None;
        // ETABS 23 can give an empty table's field list with empty entries: a null field name broke a check (0.4.57).
        fields = fields.Select(f => f ?? "").ToArray();
        if (ret != 0 && Program.Key == CsiProgram.Etabs.Key && string.Equals(key, StoryTable, StringComparison.OrdinalIgnoreCase))
            // ETABS 23.2 refused "Story Definitions" and left it out of its table list on the scratch model before the
            // analysis (0.4.59 to 0.4.61 full tests), while it read it after: the same table from the Story API.
            return StoryDefinitions(m);
        if (ret != 0)
        {
            // ETABS 23 lists tables it then refuses to read because there is nothing in them: Story Stiffness with no
            // seismic case, Steel Column Envelope with no steel columns, even with their "empty" flag off (0.4.11
            // field test). A table the program lists is then an empty table, not an error; a name it does not list
            // is still refused.
            if (Tables().Any(t => t.Key == key || t.Name == key)) return new TableData(fields, []);
            throw new EtabsException($"{Program.Name} refused to read the table '{key}' (check its name with the table list).");
        }
        var rows = new List<string[]>(Math.Max(records, 0));
        var width = fields.Length;
        records = width > 0 ? Math.Min(records, data.Length / width) : 0;
        for (var r = 0; r < records && width > 0; r++)
        {
            var row = new string[width];
            for (var c = 0; c < width; c++) row[c] = data[r * width + c] ?? "";
            rows.Add(row);
        }
        return new TableData(fields, rows);
    });

    public void RunAnalysis() => Do("run the analysis", m => { Ok(m.Analyze.RunAnalysis(), "run the analysis (the model must be saved first)"); });
    public void Save() => Do("save the model", m => { Ok(m.File.Save(""), "save the model"); });
    /// <summary>How long a tool waits for the program to redraw. The redraw only shows the change; the change is made already.</summary>
    public TimeSpan RefreshLimit { get; set; } = TimeSpan.FromSeconds(10);
    private int _refreshing;
    private volatile bool _refreshOff;

    /// <summary>
    /// Asks the program to redraw, best effort: it never fails a tool and never marks the link stuck. ETABS 23 did not
    /// come back from RefreshView after a selection (0.4.9 field test on Ahmed's PC), and the 3-minute wait then failed
    /// every later call. Now a tool waits for the redraw at most <see cref="RefreshLimit"/>; one redraw is out at a time,
    /// and after one that did not come back in time no more are asked for on this link.
    /// </summary>
    public void Refresh()
    {
        if (_refreshOff || _stuck.Length > 0 || _work.IsAddingCompleted) return;
        if (Thread.CurrentThread == _thread) { Redraw(); return; }
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        using var done = new ManualResetEventSlim();
        try { _work.Add(() => { try { Redraw(); } finally { Interlocked.Exchange(ref _refreshing, 0); try { done.Set(); } catch (ObjectDisposedException) { } } }); }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _refreshing, 0); return; }
        if (!done.Wait(RefreshLimit)) _refreshOff = true;
    }

    private void Redraw()
    {
        try { Model().View.RefreshView(0, false); }
        catch (COMException) { _model = null; }
        catch (Exception) { } // a redraw that fails changes nothing in the model
    }
}
