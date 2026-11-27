using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>
/// The Modeler's own file has one extension per program (Ahmed 2026-11-27): .cpme for ETABS, .cpms for SAP2000 and
/// .cpmf for SAFE. Saving writes the model's program's extension, every one of them opens (and the old .cpm, which holds
/// its program inside), a file of another program than the window's asks before it is converted, and the Engine with no
/// file asks first which program to work in.
/// </summary>
static class FileTypeTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static JsonObject Run(ICsiLink link, string tool, object args) =>
        (JsonObject)ModelerTools.All.Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static string Folder() => Path.Combine(Path.GetTempPath(), "cpm-types-" + Guid.NewGuid().ToString("N")[..8]);

    static void Clean(string folder) { try { Directory.Delete(folder, true); } catch (IOException) { } }

    // The first line where two model files differ, to say what was lost.
    static string FirstDifference(string expected, string actual)
    {
        string[] a = expected.Split('\n'), b = actual.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (i >= a.Length || i >= b.Length || a[i] != b[i]) return $"line {i + 1}: saved '{(i < a.Length ? a[i].Trim() : "")}', read '{(i < b.Length ? b[i].Trim() : "")}'";
        return "";
    }

    static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    // Every kind of model the Modeler makes or opens: its templates and the programs' own files of the samples.
    static IEnumerable<(string Name, StructuralModel Model)> Models()
    {
        yield return ("an empty model", ModelEdit.New("Empty"));
        yield return ("a concrete frame", Templates.ConcreteFrame("Frame", [6, 5], [4], 3, 3.2));
        foreach (var name in new[] { "etabs23-default.e2k", "etabs-RC-PN-v20.e2k", "etabs-PT-SL-v20.e2k", "safe-RC-PN-v23.f2k", "safe-PT-SL-v23.f2k" })
            if (File.Exists(Sample(name))) yield return (name, OfflineEtabs.Load(Sample(name)));
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("file types: one extension per program, and the old .cpm still a model file", () =>
        {
            Check(ModelFile.ExtensionFor("etabs") == ".cpme" && ModelFile.ExtensionFor("sap2000") == ".cpms" && ModelFile.ExtensionFor("safe") == ".cpmf", "the extension of each program");
            Check(ModelFile.ExtensionFor("") == ".cpme" && ModelFile.ExtensionFor(null) == ".cpme", "no program is ETABS, as the model's own default");
            foreach (var x in new[] { "a.cpme", "a.CPMS", "a.cpmf", "a.cpm" }) Check(ModelFile.IsModelFile(x), x + " is a model file");
            foreach (var x in new[] { "a.cpmx", "a.e2k", "a.s2k", "a.cpmz" }) Check(!ModelFile.IsModelFile(x), x + " is not a model file");
            Check(ModelFile.TargetOfExtension("a.cpms") == "sap2000" && ModelFile.TargetOfExtension("a.cpmf") == "safe" && ModelFile.TargetOfExtension("a.cpme") == "etabs" && ModelFile.TargetOfExtension("a.cpm") is null, "the program each extension names");
            foreach (var (file, program) in new[] { ("a.e2k", "etabs"), ("a.$et", "etabs"), ("a.s2k", "sap2000"), ("a.$2k", "sap2000"), ("a.f2k", "safe"), ("a.$fk", "safe"), ("a.cpmf", "safe") })
                Check(ModelFile.ProgramOf(file) == program, $"{file} is a file of {program}");
            Check(ModelFile.ProgramOf("a.xlsx") is null, "a workbook is no program's model");
        });

        yield return ("file types: every kind of model saves its own program's extension, and opens back exactly, for ETABS, SAP2000 and SAFE", () =>
        {
            var folder = Folder();
            try
            {
                var i = 0;
                foreach (var (name, model) in Models())
                    foreach (var target in StructuralModel.Targets)
                    {
                        var m = ModelFile.FromJson(ModelFile.ToJson(model));
                        m.Target = target;
                        var link = new OfflineEtabs(m);
                        var before = ModelFile.ToJson(m);
                        var e2k = link.SaveAs(Path.Combine(folder, $"M{i++}.e2k"));
                        var own = ModelFile.PathFor(e2k, target);
                        Check(File.Exists(own), $"{name}, {target}: no {ModelFile.ExtensionFor(target)} was written");
                        foreach (var other in ModelFile.Extensions.Where(x => x != ModelFile.ExtensionFor(target)))
                            Check(!File.Exists(Path.ChangeExtension(e2k, other)), $"{name}, {target}: a {other} was written too");
                        Check(ModelFile.Beside(e2k) == own && ModelerTables.ModelerFileBeside(e2k) == own, $"{name}, {target}: the file beside the .e2k is not found");
                        var opened = OfflineEtabs.Open(own);
                        Check(opened.Exact && opened.Model.Target == target, $"{name}, {target}: it opened for {opened.Model.Target}");
                        Check(ModelFile.ToJson(opened.Model) == before, $"{name}, {target}: the model did not come back exactly: " + FirstDifference(before, ModelFile.ToJson(opened.Model)));
                        Check(OfflineEtabs.SavesTo(own, opened) == e2k, $"{name}, {target}: it saves to {OfflineEtabs.SavesTo(own, opened)}, not beside itself");
                    }
                Check(i >= 6, "too few kinds of models were tried: " + i);
            }
            finally { Clean(folder); }
        });

        yield return ("file types: an old .cpm opens for the program it holds, and the next save writes the new extension beside it, keeping the old file", () =>
        {
            var folder = Folder();
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var target in StructuralModel.Targets)
                {
                    var m = Templates.ConcreteFrame("Old " + target, [6], [5], 2, 3.0);
                    m.Target = target;
                    var old = Path.Combine(folder, "Old " + target + ".cpm");
                    ModelFile.Save(m, old);
                    var bytes = File.ReadAllBytes(old);
                    var link = new OfflineEtabs();
                    var answer = Run(link, "open_model_file", new { path = old });
                    Check(link.Model.Target == target && answer["program"]!.GetValue<string>() == StructuralModel.TargetName(target), $"the old .cpm of {target} opened for {link.Model.Target}");
                    Check(ModelFile.ToJson(link.Model) == ModelFile.ToJson(m), $"{target}: the old .cpm did not open exactly");
                    link.Save();
                    var now = Path.ChangeExtension(old, ModelFile.ExtensionFor(target));
                    Check(link.Path == Path.ChangeExtension(old, ".e2k"), $"{target}: the save went to {link.Path}, not beside the old file");
                    Check(File.Exists(now), $"{target}: no {ModelFile.ExtensionFor(target)} beside the old .cpm");
                    Check(File.Exists(old) && File.ReadAllBytes(old).SequenceEqual(bytes), $"{target}: the old .cpm was changed or removed");
                    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
                    Check(ModelFile.Beside(link.Path) == now, $"{target}: the file the save wrote is not the one found beside the .e2k");
                    var cli = ModelFile.Load(old);
                    Check(cli.Target == target, $"{target}: with no window the old .cpm keeps its program");
                }
                // An old .cpm alone beside a program file is found too.
                var lone = Path.Combine(folder, "Lone.e2k");
                ModelFile.Save(ModelEdit.New("Lone"), Path.ChangeExtension(lone, ".cpm"));
                Check(ModelFile.Beside(lone) == Path.ChangeExtension(lone, ".cpm") && ModelerTables.ModelerFileBeside(lone) is not null, "an old .cpm beside a program file is not found");
                Check(ModelFile.Beside(Path.Combine(folder, "None.e2k")) is null, "a file with nothing beside it found one");
            }
            finally { Clean(folder); }
        });

        yield return ("file types: a SAP2000 or SAFE file the Modeler saved saves to the same place again, its own file found beside it", () =>
        {
            var folder = Folder();
            try
            {
                foreach (var (target, text) in new[] { ("sap2000", ".s2k"), ("safe", ".f2k") })
                {
                    var m = Templates.ConcreteFrame("P " + target, [6, 6], [5], 2, 3.0);
                    m.Target = target;
                    var link = new OfflineEtabs(m);
                    var e2k = link.SaveAs(Path.Combine(folder, "P " + target + ".e2k"));
                    var own = Path.ChangeExtension(e2k, text);
                    Check(File.Exists(own), $"{target}: no {text} was written");
                    var opened = OfflineEtabs.Open(own);
                    Check(OfflineEtabs.SavesTo(own, opened) == e2k, $"{target}: the {text} does not save to its .e2k again");
                }
            }
            finally { Clean(folder); }
        });

        yield return ("file types: a file of another program than the window's asks before it is converted; with no window it keeps its own program", () =>
        {
            var folder = Folder();
            try
            {
                var sap = Templates.ConcreteFrame("S", [6], [5], 2, 3.0);
                sap.Target = "sap2000";
                var saved = new OfflineEtabs(sap).SaveAs(Path.Combine(folder, "S.e2k"));
                var cpms = ModelFile.PathFor(saved, "sap2000");
                var link = new OfflineEtabs();
                var before = ModelFile.ToJson(link.Model);
                var revision = link.Revision;
                var ask = Run(link, "open_model_file", new { path = cpms, for_program = "etabs" });
                Check(ask["needs_choice"]!.GetValue<bool>() && ask["file_program"]!.GetValue<string>() == "SAP2000" && ask["window_program"]!.GetValue<string>() == "ETABS", "the SAP2000 file was not recognised: " + ask.ToJsonString());
                Check(ModelFile.ToJson(link.Model) == before && link.Revision == revision, "the model was replaced before the person chose");
                var same = Run(link, "open_model_file", new { path = cpms, for_program = "sap2000" });
                Check(same["needs_choice"] is null && link.Model.Target == "sap2000", "a file of the window's own program asked");
                var converted = Run(link, "open_model_file", new { path = cpms, for_program = "safe", convert = true });
                Check(link.Model.Target == "safe" && converted["converted_from"]!.GetValue<string>() == "SAP2000" && link.Unsaved, "convert: the model is not made for SAFE, or not marked unsaved");
                Check(link.Model.LineAssigns.Count == sap.LineAssigns.Count && link.Model.AreaAssigns.Count == sap.AreaAssigns.Count, "convert lost objects");
                link.Save();
                Check(File.Exists(Path.ChangeExtension(saved, ".cpmf")) && File.Exists(cpms), "the converted model saves its .cpmf, and the .cpms stays");
                Run(link, "open_model_file", new { path = cpms, for_program = "etabs", convert = false });
                Check(link.Model.Target == "sap2000", "keep: the file's own program");
                Run(link, "open_model_file", new { path = cpms });
                Check(link.Model.Target == "sap2000", "with no window asking, the file keeps its program");
                // A program's own text file is recognised as well.
                var f2k = Path.Combine(folder, "From SAFE.f2k");
                File.Copy(Sample("safe-RC-PN-v23.f2k"), f2k);
                var safe = Run(link, "open_model_file", new { path = f2k, for_program = "etabs" });
                Check(safe["needs_choice"]!.GetValue<bool>() && safe["file_program"]!.GetValue<string>() == "SAFE", "a SAFE .f2k in an ETABS window was not recognised");
            }
            finally { Clean(folder); }
        });

        yield return ("file types: the Engine started with no file asks which program first; the choice is no change to undo or save", () =>
        {
            HandModel.Reset();
            var link = new OfflineEtabs { ChooseProgram = true };
            JsonObject Ask(string json) => HandModel.Handle(link, JsonDocument.Parse(json).RootElement);
            var first = Ask("""{"tool":"model_definitions","args":{}}""");
            Check(first["choose_program"]?.GetValue<bool>() == true, "the window is not told to ask");
            var bad = Ask("""{"start_program":"revit"}""");
            Check(!bad["ok"]!.GetValue<bool>() && link.ChooseProgram, "another program was taken");
            var chose = Ask("""{"start_program":"safe"}""");
            Check(chose["ok"]!.GetValue<bool>() && link.Model.Target == "safe" && !link.Unsaved && chose["undo"] is null, "the choice was not taken, or became a change: " + chose.ToJsonString());
            Check(Ask("""{"tool":"model_definitions","args":{}}""")["choose_program"] is null, "the window asks again");
            var opened = new OfflineEtabs { ChooseProgram = true };
            opened.Replace(ModelEdit.New("F"), "");
            Check(!opened.ChooseProgram, "a model put in the window still asks");
            var set = new OfflineEtabs { ChooseProgram = true };
            Run(set, "set_model_program", new { program = "sap2000" });
            Check(!set.ChooseProgram, "File > Model For still leaves the question");
            Check(!new OfflineEtabs().ChooseProgram, "a window given a model asks");
            HandModel.Reset();
        });

        yield return ("file types: the window's File > Open shows the program's own files first, Save As writes its extension, and nothing asks with a browser alert", () =>
        {
            var html = File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));
            foreach (var part in new[]
            {
                "const progExt = {etabs: \"cpme\", sap2000: \"cpms\", safe: \"cpmf\"};",
                "const progText = {etabs: [\"e2k\", \"$et\"], sap2000: [\"s2k\", \"$2k\"], safe: [\"f2k\", \"$fk\"]};",
                "All CSI ProMax files",
                "filter: modelFilter(program)",
                "async function hmOpenFile(path, d)",
                "for_program: program",
                "function hmChooseProgram()",
                "if (hm.state && hm.state.choose_program) hmChooseProgram();",
                "hmPost({start_program: k})",
            })
                Check(html.Contains(part, StringComparison.Ordinal), "viewer.html lacks: " + part);
            var open = html[html.IndexOf("async function hmOpenFile", StringComparison.Ordinal)..html.IndexOf("function hmOpenTyped", StringComparison.Ordinal)];
            Check(!open.Contains("alert(", StringComparison.Ordinal) && !open.Contains("confirm(", StringComparison.Ordinal), "a browser alert or confirm asks");
            Check(open.Contains("dialog(\"Open Model\"", StringComparison.Ordinal), "the question is not in the page's own form");
            var save = html[html.IndexOf("async function hmSave", StringComparison.Ordinal)..html.IndexOf("async function hmExport", StringComparison.Ordinal)];
            Check(save.Contains("ext = progExt[k]", StringComparison.Ordinal), "Save As does not use the program's extension");
            Check(CsiProMax.Assistant.ModelerCheck.Script.Contains("asks before converting", StringComparison.Ordinal), "the window check does not try a file of another program");
        });

        yield return ("file types: install.ps1 registers .cpme, .cpms, .cpmf and the old .cpm for the current user, each opening in the Engine", () =>
        {
            var ps = File.ReadAllText(Path.Combine(SourceText.Root(), "install.ps1"));
            foreach (var ext in new[] { ".cpme", ".cpms", ".cpmf", ".cpm" })
                Check(ps.Contains($"Ext = '{ext}'", StringComparison.Ordinal), "install.ps1 does not register " + ext);
            var part = ps[ps.IndexOf("# 3c.", StringComparison.Ordinal)..ps.IndexOf("# 4.", StringComparison.Ordinal)];
            Check(part.Contains(@"HKCU:\Software\Classes", StringComparison.Ordinal) && !part.Contains("HKLM:", StringComparison.Ordinal) && !part.Contains("HKCR:", StringComparison.Ordinal), "the file types are not the current user's only");
            Check(ps.Contains("--modeler \"%1\"", StringComparison.Ordinal), "a double-click does not open the file in the Engine");
            var tests = File.ReadAllText(Path.Combine(SourceText.Root(), "tests", "install.Tests.ps1"));
            Check(tests.Contains("-ClassesRoot", StringComparison.Ordinal), "install.Tests.ps1 does not check the file types");
        });
    }
}
