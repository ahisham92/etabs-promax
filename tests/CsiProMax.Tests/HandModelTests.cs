using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>
/// Modelling by hand in the Modeler's window (HandModel, HandTools): the same tools as the chat, the Define forms'
/// add, change and delete, undo and redo, and the page's own menus and forms. Ahmed: modelling by hand must be as
/// clear and easy as in the chat, and define everything the way SAP2000 does.
/// </summary>
static class HandModelTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    static JsonObject Do(ICsiLink link, string tool, string args) =>
        HandModel.Handle(link, JsonDocument.Parse($$"""{"tool":"{{tool}}","args":{{args}}}""").RootElement);
    static JsonObject Ok(ICsiLink link, string tool, string args)
    {
        var a = Do(link, tool, args);
        Check(a["ok"]!.GetValue<bool>(), $"{tool} failed: {a["error"]}");
        return a;
    }
    static string Error(ICsiLink link, string tool, string args)
    {
        var a = Do(link, tool, args);
        Check(!a["ok"]!.GetValue<bool>(), $"{tool} should have been refused");
        return a["error"]!.GetValue<string>();
    }
    static JsonObject Undo(ICsiLink link, bool redo = false) => HandModel.Handle(link, JsonDocument.Parse(redo ? """{"redo":true}""" : """{"undo":true}""").RootElement);

    /// <summary>A Modeler with a 2 x 1 bay grid, two stories, and nothing drawn.</summary>
    static OfflineEtabs Grid()
    {
        HandModel.Reset();
        var link = new OfflineEtabs();
        Ok(link, "define_stories", """{"stories":[{"name":"L1","height":3},{"name":"L2","height":3}]}""");
        Ok(link, "define_grids", """{"grids":[{"label":"A","direction":"X","coordinate":0},{"label":"B","direction":"X","coordinate":6},{"label":"C","direction":"X","coordinate":12},{"label":"1","direction":"Y","coordinate":0},{"label":"2","direction":"Y","coordinate":5}]}""");
        return link;
    }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("hand model: the window draws with the chat's own tools, on one story or all", () =>
        {
            var m = Grid();
            Ok(m, "add_columns", """{"points":[[0,0],[6,0],[12,0],[0,5],[6,5],[12,5]],"stories":["L1","L2"],"section":"ConcCol"}""");
            Ok(m, "add_beams", """{"lines":[[0,0,6,0],[6,0,12,0]],"stories":["L2"],"section":"ConcBm"}""");
            Ok(m, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"stories":["L1","L2"],"section":"Slab1"}""");
            Equal(14, m.Model.LineAssigns.Count, "12 columns and 2 beams"); Equal(2, m.Model.AreaAssigns.Count, "a floor on each story");
            Ok(m, "set_supports", """{"type":"fixed","at_base":true}""");
            Equal(6, m.Model.PointAssigns.Count(p => p.Restraint is not null), "a support under each column");
            Ok(m, "set_loads", """{"areas":["F1@L1","F1@L2"],"pattern":"Live","value":3}""");
            Equal(2, m.Model.AreaLoads.Count, "the floors are loaded");
            Ok(m, "delete_objects", """{"frames":["B1@L2"]}""");
            Equal(13, m.Model.LineAssigns.Count, "deleted by hand, with no question (Undo brings it back)");
            Check(Error(new MemoryEtabs(), "add_columns", """{"points":[[0,0]],"section":"C"}""").Contains("Modeler"), "only in the Modeler, never in a CSI program");
            Check(Error(m, "run_etabs_code", "{}").Contains("no Modeler command"), "no code from the page");
            Check(Error(m, "run_analysis", "{}").Contains("no Modeler command"), "no analysis from the page");
        });

        yield return ("hand model: beams, slabs and joints go on the base level when it is named; columns and walls there say why", () =>
        {
            // A plan view at the base level (Move Up from the top story comes round to it) draws there too.
            var m = Grid();
            var b = m.Stories()[0].Name;
            Ok(m, "add_beams", $$"""{"lines":[[0,0,6,0]],"stories":["{{b}}"],"section":"ConcBm"}""");
            Ok(m, "add_slabs", $$"""{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"stories":["{{b}}"],"section":"Slab1"}""");
            Ok(m, "add_joints", $$"""{"points":[[12,5]],"stories":["{{b}}"]}""");
            Equal(1, m.Model.LineAssigns.Count(l => l.Story == b), "a grade beam on the base level");
            Equal(1, m.Model.AreaAssigns.Count(x => x.Story == b), "a mat on the base level");
            var why = Error(m, "add_columns", $$"""{"points":[[0,0]],"stories":["{{b}}"],"section":"ConcCol"}""");
            Check(why.Contains("bottom level") && why.Contains("L1"), "a column on the base level says why, and where it can go: " + why);
            Check(Error(m, "add_walls", $$"""{"lines":[[0,0,6,0]],"stories":["{{b}}"],"section":"Slab1"}""").Contains("bottom level"), "a wall on the base level says why too");
            Ok(m, "add_beams", """{"lines":[[0,5,6,5]],"section":"ConcBm"}""");
            Equal(1, m.Model.LineAssigns.Count(l => l.Story == b), "with no story named, beams go on every story above the base, not on it");
        });

        yield return ("work planes: by three points, two frames or an area; renamed and deleted; in the Modeler's file only; in step with the units", () =>
        {
            var m = Grid(); // L1 at 3 m
            Ok(m, "add_columns", """{"points":[[0,0],[6,0]],"stories":["L1"],"section":"ConcCol"}""");
            Ok(m, "add_beams", """{"lines":[[0,0,6,0],[0,5,6,5]],"stories":["L1"],"section":"ConcBm"}""");
            Ok(m, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"stories":["L1"],"section":"Slab1"}""");
            var frames = Physical.Frames(m.Model).ToList();
            static string Name(PhysicalFrame f) => f.Label + "@" + f.Story;
            var column = frames.First(f => f.Kind == LineKind.Column && Math.Abs(f.I.X) < 1e-9);
            var column6 = frames.First(f => f.Kind == LineKind.Column && Math.Abs(f.I.X - 6) < 1e-9);
            var beam0 = frames.First(f => f.Kind == LineKind.Beam && Math.Abs(f.I.Y) < 1e-9);
            var beam5 = frames.First(f => f.Kind == LineKind.Beam && Math.Abs(f.I.Y - 5) < 1e-9);
            var floor = Physical.Areas(m.Model).First();

            Ok(m, "define_work_plane", """{"name":"Sloped","points":[[0,0,0],[6,0,0],[0,5,3]]}""");
            Ok(m, "define_work_plane", $$"""{"name":"Level L1","frames":["{{Name(beam0)}}","{{Name(beam5)}}"]}""");
            Ok(m, "define_work_plane", $$"""{"name":"Grid 1","frames":["{{Name(column)}}","{{Name(beam0)}}"]}""");
            Ok(m, "define_work_plane", $$"""{"name":"Slab","area":"{{floor.Label}}@{{floor.Story}}"}""");
            Ok(m, "define_work_plane", $$"""{"name":"Grid B","frame":"{{Name(column6)}}","point":[6,5,3]}""");
            WorkPlane Plane(string n) => m.Model.WorkPlanes!.Single(w => w.Name == n);
            Equal(5, m.Model.WorkPlanes!.Count, "five work planes");
            Check(Math.Abs(WorkPlanes.Normal(Plane("Grid B"))[0]) > 0.999999, "a column and a point off it make the plane of their grid line (x = 6)");
            Check(Error(m, "define_work_plane", $$"""{"name":"Bad","frame":"{{Name(column6)}}","point":[6,0,1.5]}""").Contains("on the frame's line"), "a point on the frame's line makes no plane");
            Check(Error(m, "define_work_plane", $$"""{"name":"Bad","frame":"{{Name(column6)}}"}""").Contains("give the point"), "a frame needs its point");
            Ok(m, "delete_definition", """{"kind":"work_plane","name":"Grid B"}""");
            Check(Math.Abs(WorkPlanes.Normal(Plane("Level L1"))[2]) > 0.999999, "two parallel beams on a story make a level plane");
            Check(Math.Abs(WorkPlanes.Normal(Plane("Grid 1"))[1]) > 0.999999, "a column and the beam it meets make the plane of their grid line");
            Check(Math.Abs(WorkPlanes.Normal(Plane("Slab"))[2]) > 0.999999 && WorkPlanes.Distance(Plane("Slab"), new Xyz(3, 2, 3)) < 1e-9, "a floor makes the level plane it lies in");

            Check(Error(m, "define_work_plane", """{"name":"Bad","points":[[0,0,0],[1,1,1],[2,2,2]]}""").Contains("one line"), "three points on one line make no plane");
            Check(Error(m, "define_work_plane", $$"""{"name":"Bad","frames":["{{Name(column6)}}","{{Name(beam5)}}"]}""").Contains("skew"), "two skew frames make no plane");
            Check(Error(m, "define_work_plane", $$"""{"name":"Bad","frames":["{{Name(beam0)}}","{{Name(beam0)}}"]}""").Contains("one line"), "a frame with itself makes no plane");
            Check(Error(m, "define_work_plane", """{"name":"Sloped","points":[[0,0,0],[6,0,0],[0,5,1]]}""").Contains("already"), "names are the planes' own");
            Check(Error(m, "define_work_plane", """{"name":"New"}""").Contains("Give the plane"), "a new plane needs its points, frames or area");

            Ok(m, "define_work_plane", """{"name":"Ramp","was":"Sloped"}""");
            Check(m.Model.WorkPlanes!.Exists(w => w.Name == "Ramp") && !m.Model.WorkPlanes.Exists(w => w.Name == "Sloped"), "renamed, keeping its plane");
            Ok(m, "delete_definition", """{"kind":"work_plane","name":"Ramp"}""");
            Equal(3, m.Model.WorkPlanes!.Count, "deleted");
            Check(Error(m, "delete_definition", """{"kind":"work_plane","name":"Ramp"}""").Contains("no work plane"), "a plane that is not there");
            var defs = Ok(m, "model_definitions", "{}")["result"]!["work_planes"]!.AsArray();
            Equal(3, defs.Count, "the Define form lists them");

            var json = ModelFile.ToJson(m.Model);
            Check(json.Contains("\"workPlanes\"") && json.Contains("Grid 1"), "kept in the Modeler's file");
            Equal(3, ModelFile.FromJson(json).WorkPlanes!.Count, "and read back from it");
            Check(!CsiProMax.E2k.E2kWriter.Write(m.Model).Contains("Grid 1"), "not written to the .e2k");
            var ft = ModelUnits.Convert(m.Model, new CsiProMax.Model.Units { Force = "kip", Length = "ft", Temperature = "F" });
            Check(Math.Abs(ft.WorkPlanes!.Single(w => w.Name == "Level L1").Points[0][2] - 3 / 0.3048) < 1e-6, "a units change turns the planes' points too");
        });

        yield return ("drawing by points at story levels: frames and areas take their type from their inclination (Options > Tolerances), one object across stories; ends off the levels refused", () =>
        {
            var m = Grid(); // Base 0, L1 3, L2 6
            List<string> Names(JsonNode r, string key) => r["result"]![key]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            var f = Names(Ok(m, "add_frames_by_points", """{"frames":[[0,0,3,6,0,3],[0,0,0,0,0,6],[0,0,0,6,0,6],[6,0,6,12,0,3],[0,5,0,12,5,3],[12,5,0,12.03,5,3]],"section":"ConcCol"}"""), "frames");
            LineObject L(int i) => m.Model.Line(f[i].Split('@')[0])!;
            Check(L(0).Kind == LineKind.Beam && f[0].EndsWith("@L1"), "two ends on one level make a beam on it");
            Check(L(1).Kind == LineKind.Column && L(1).StoriesBelowI == 2 && f[1].EndsWith("@L2"), "one end above the other makes a column, one object from the base to L2");
            Check(L(2).Kind == LineKind.Brace && L(2).StoriesBelowI == 2 && f[2].EndsWith("@L2"), "45 degrees makes a brace, across both stories");
            var down = Physical.Frames(m.Model).Single(x => x.Label + "@" + x.Story == f[3]);
            Check(L(3).Kind == LineKind.Brace && down.I == new Xyz(12, 0, 3) && down.J == new Xyz(6, 0, 6), "a brace drawn downwards runs from its lower end up");
            Check(L(4).Kind == LineKind.Beam && L(4).StoriesBelowI == 1 && f[4].EndsWith("@L1"), "14 degrees from horizontal is within the 20 of Options > Tolerances: a sloped beam, from the base to L1");
            Check(L(5).Kind == LineKind.Column && L(5).PointI != L(5).PointJ, "0.6 degrees from vertical is within the 1: a column, leaning");
            Ok(m, "set_program_form", """{"table":"Preferences - Tolerance","values":{"MaxInclHorzBmFlr":"10","MaxInclVertCol":"0.5"}}""");
            Check(m.Model.Inclinations() == (0.5, 10), "the tolerances are read from Options > Tolerances");
            var again = Names(Ok(m, "add_frames_by_points", """{"frames":[[6,5,0,18,5,3],[0,5,0,0.03,5,3]],"section":"ConcCol"}"""), "frames");
            Check(m.Model.Line(again[0].Split('@')[0])!.Kind == LineKind.Brace && m.Model.Line(again[1].Split('@')[0])!.Kind == LineKind.Brace, "with 10 and 0.5 degrees the sloped beam and the leaning column are braces");
            Ok(m, "set_program_form", """{"table":"Preferences - Tolerance","values":{"MaxInclHorzBmFlr":"","MaxInclVertCol":""}}""");
            Check(m.Model.Inclinations() == (1, 20), "blank is ETABS's default, 1 and 20");
            Check(Error(m, "add_frames_by_points", """{"frames":[[0,0,1.5,6,0,3]],"section":"ConcCol"}""").Contains("not a story level"), "an end between levels is refused");
            Check(Error(m, "add_frames_by_points", """{"frames":[[6,5,3,6,5,3]],"section":"ConcCol"}""").Contains("two different ends"), "a frame needs two ends");
            Check(Error(m, "add_frames_by_points", """{"frames":[[0,0,3,6,0]],"section":"ConcCol"}""").Contains("6 numbers"), "each frame is six numbers");

            var a = Names(Ok(m, "add_areas_by_points", """{"areas":[[[0,0,3],[6,0,3],[6,5,3],[0,5,3]],[[0,0,0],[6,0,0],[6,0,6],[0,0,6]],[[0,0,0],[6,0,0],[6,5,3],[0,5,3]],[[12,0,0],[12,5,0],[12,5,6]],[[0,0,0],[12,0,0],[12,12,3],[0,12,3]]],"section":"Slab1","wall_section":"Wall1"}"""), "areas");
            AreaAssign S(int i) => m.Model.AreaAssigns.Single(x => x.Label + "@" + x.Story == a[i]);
            AreaObject A(int i) => m.Model.Area(a[i].Split('@')[0])!;
            Check(A(0).Kind == AreaKind.Floor && A(0).StoriesBelow.All(b => b == 0) && a[0].EndsWith("@L1"), "corners on one level make a floor on it");
            Check(A(1).Kind == AreaKind.Wall && A(1).StoriesBelow.SequenceEqual([2, 2, 0, 0]) && a[1].EndsWith("@L2"), "upright, a wall: one object from the base to L2");
            Check(A(2).Kind == AreaKind.Ramp && A(2).StoriesBelow.SequenceEqual([1, 1, 0, 0]) && a[2].EndsWith("@L1"), "sloped, a ramp");
            Check(A(3).Kind == AreaKind.Wall && A(3).StoriesBelow.SequenceEqual([2, 2, 0]), "a three-cornered wall too");
            Check(A(4).Kind == AreaKind.Floor && A(4).StoriesBelow.SequenceEqual([1, 1, 0, 0]), "14 degrees from level is within the 20: a sloped floor, from the base to L1");
            Check(S(1).Section == "Wall1" && S(3).Section == "Wall1" && S(0).Section == "Slab1" && S(2).Section == "Slab1" && S(4).Section == "Slab1", "the walls take the wall section, the floors and the ramp the other");
            var ramp = Physical.Areas(m.Model).Single(x => x.Label + "@" + x.Story == a[2]);
            Check(ramp.Corners.SequenceEqual([new Xyz(0, 0, 0), new Xyz(6, 0, 0), new Xyz(6, 5, 3), new Xyz(0, 5, 3)]), "the ramp's corners are where they were drawn");
            Check(Error(m, "add_areas_by_points", """{"areas":[[[0,0,0],[6,0,0],[6,5,3],[0,5,0]]],"section":"Slab1"}""").Contains("not flat"), "corners not in one plane are refused");
            Check(Error(m, "add_areas_by_points", """{"areas":[[[0,0,0],[6,0,0],[6,5,1]]],"section":"Slab1"}""").Contains("not a story level"), "a corner between levels is refused");
            Check(Error(m, "add_areas_by_points", """{"areas":[[[0,0,3],[6,0,3],[6,5,3],[6,0,3]]],"section":"Slab1"}""").Contains("at one place"), "two corners at one place are refused");
            Check(Error(m, "add_areas_by_points", """{"areas":[[[0,0,3],[6,0,3],[6,5,3]]],"section":"Nope"}""").Contains("no slab or wall section"), "the section must be there");

            var back = CsiProMax.E2k.E2kReader.Read(CsiProMax.E2k.E2kWriter.Write(m.Model));
            Check(back.Lines.Exists(l => l.Kind == LineKind.Brace && l.StoriesBelowI == 2) && back.Lines.Exists(l => l.Kind == LineKind.Beam && l.StoriesBelowI == 1) && back.Areas.Exists(x => x.Kind == AreaKind.Wall && x.StoriesBelow.SequenceEqual([2, 2, 0, 0])) && back.Areas.Exists(x => x.Kind == AreaKind.Ramp),
                "the .e2k keeps them across their stories");
        });

        yield return ("hand model: undo and redo step through every hand change, and stop at the chat's", () =>
        {
            var m = Grid();
            Ok(m, "add_columns", """{"points":[[0,0]],"stories":["L1"],"section":"ConcCol"}""");
            var a = Ok(m, "add_columns", """{"points":[[6,0]],"stories":["L1"],"section":"ConcCol"}""");
            Check(a["undo"]!.GetValue<string>().Contains("columns"), "the Edit menu names what Undo undoes");
            Equal(2, m.Model.LineAssigns.Count, "two columns");
            var u = Undo(m);
            Check(u["ok"]!.GetValue<bool>() && u["redo"] is not null, "undone, and it can be redone"); Equal(1, m.Model.LineAssigns.Count, "one column after undo");
            Undo(m); Equal(0, m.Model.LineAssigns.Count, "none after two undos");
            Undo(m, redo: true); Undo(m, redo: true); Equal(2, m.Model.LineAssigns.Count, "both back after two redos");
            Check(m.Unsaved, "an undo or redo counts as a change to save");
            // Undo after the chat changed the model would throw the chat's work away: refused.
            m.AddFrame(0, 0, 3, 6, 0, 3, "ConcBm");
            var refused = Undo(m);
            Check(!refused["ok"]!.GetValue<bool>() && refused["error"]!.GetValue<string>().Contains("chat"), "undo after the chat's change is refused and says why");
            Equal(3, m.Model.LineAssigns.Count, "nothing was undone");
            // A new hand change starts the history again; reading never adds a step.
            Ok(m, "add_columns", """{"points":[[12,0]],"stories":["L1"],"section":"ConcCol"}""");
            var steps = Ok(m, "model_definitions", "{}")["undo"]!.GetValue<string>();
            Check(steps.Contains("columns"), "reading the definitions is not a step");
            Check(Undo(m)["ok"]!.GetValue<bool>(), "undo works again after a new hand change");
            HandModel.Reset();
            Check(!Undo(m)["ok"]!.GetValue<bool>(), "nothing to undo after a reset");
        });

        yield return ("hand model: story data renames, changes heights, adds and deletes stories with what stands on them", () =>
        {
            var m = Grid();
            Ok(m, "add_columns", """{"points":[[0,0]],"stories":["L1","L2"],"section":"ConcCol"}""");
            Ok(m, "define_stories", """{"base_name":"Base","base_elevation":-1,"stories":[{"name":"Ground","height":4,"was":"L1"},{"name":"Roof","height":3.5}]}""");
            Equal("Base,Ground,Roof", string.Join(",", m.Model.Stories.Select(s => s.Name)), "story names");
            Equal(3.0, m.Model.Elevation(1), "elevations follow the base and the heights");
            Equal(1, m.Model.LineAssigns.Count, "the column on the deleted story L2 went with it");
            Equal("Ground", m.Model.LineAssigns[0].Story, "the column on L1 follows its new name");
            Check(Error(m, "define_stories", """{"stories":[{"name":"A","height":0}]}""").Contains("height"), "a story needs a height");
            Check(Error(m, "define_stories", """{"stories":[{"name":"A","height":3},{"name":"A","height":3}]}""").Contains("Two stories"), "names are unique");
        });

        yield return ("hand model: grid data is replaced as the form shows it", () =>
        {
            var m = Grid();
            Ok(m, "define_grids", """{"grids":[{"label":"A","direction":"X","coordinate":0},{"label":"B","direction":"X","coordinate":8},{"label":"1","direction":"Y","coordinate":0}]}""");
            Equal("A:X:0,B:X:8,1:Y:0", string.Join(",", m.Model.Grids.Select(g => $"{g.Label}:{g.Direction}:{g.Coordinate}")), "grid lines");
            Check(Error(m, "define_grids", """{"grids":[{"label":"A","direction":"X","coordinate":0},{"label":"A","direction":"Y","coordinate":0}]}""").Contains("Two grid"), "labels are unique");
        });

        yield return ("hand model: materials and sections are added, changed, renamed and deleted the way SAP2000's forms do", () =>
        {
            var m = Grid();
            Ok(m, "define_material", """{"name":"C40","fc":40000,"elastic_modulus":35000000,"unit_weight":25}""");
            Ok(m, "define_frame_section", """{"name":"C600","kind":"column","depth":0.6,"width":0.6,"material":"C40"}""");
            Ok(m, "add_columns", """{"points":[[0,0]],"stories":["L1"],"section":"C600"}""");
            Ok(m, "define_frame_section", """{"name":"C650","was":"C600","kind":"column","depth":0.65,"width":0.6,"material":"C40"}""");
            Equal("C650", m.Model.LineAssigns[0].Section, "the column keeps its section under the new name");
            Equal(0.65, m.Model.FrameSections.Find(s => s.Name == "C650")!.Number("D"), "the new depth");
            Check(m.Model.Passthrough.Find(s => s.Heading == "CONCRETE SECTIONS")!.Lines.Any(l => l.Contains("\"C650\"")), "ETABS's reinforcement line follows the name");
            Ok(m, "define_material", """{"name":"C45","was":"C40","fc":45000,"elastic_modulus":36000000}""");
            Equal("C45", m.Model.FrameSections.Find(s => s.Name == "C650")!.Material, "the section follows the material's new name");
            Check(Error(m, "delete_definition", """{"kind":"frame_section","name":"C650"}""").Contains("used by 1"), "a section in use is not deleted");
            Check(Error(m, "delete_definition", """{"kind":"material","name":"C45"}""").Contains("used by"), "a material in use is not deleted");
            Ok(m, "define_shell_section", """{"name":"S250","kind":"slab","thickness":0.25}""");
            Ok(m, "define_shell_section", """{"name":"S260","was":"S250","kind":"slab","thickness":0.26}""");
            Equal(0.26, m.Model.ShellSections.Find(s => s.Name == "S260")!.Thickness, "slab thickness changed");
            Ok(m, "delete_definition", """{"kind":"shell_section","name":"S260"}""");
            Check(!m.Model.ShellSections.Exists(s => s.Name == "S260"), "an unused slab section is deleted");
            Check(Error(m, "define_frame_section", """{"name":"ConcBm","kind":"beam","depth":0.5,"width":0.3}""").Contains("already"), "no two sections with one name");
            var d = Ok(m, "model_definitions", "{}")["result"]!;
            Check(d["frame_sections"]!.AsArray().Any(s => s!["name"]!.GetValue<string>() == "C650" && s["used"]!.GetValue<int>() == 1 && s["editable"]!.GetValue<bool>()), "the forms see each section, its size and its use");
            Check(d["materials"]!.AsArray().Any(x => x!["name"]!.GetValue<string>() == "C45" && x["fc"]!.GetValue<double>() == 45000), "and each material's strength");
        });

        yield return ("hand model: load patterns and combinations are defined, renamed and deleted the way SAP2000 does", () =>
        {
            var m = Grid();
            Ok(m, "add_slabs", """{"outlines":[[[0,0],[6,0],[6,5],[0,5]]],"stories":["L1"],"section":"Slab1"}""");
            Ok(m, "define_load_pattern", """{"name":"SDL","type":"Super Dead","self_weight":0}""");
            Ok(m, "set_loads", """{"areas":["F1@L1"],"pattern":"SDL","value":1.5}""");
            Ok(m, "define_combination", """{"name":"ULS","type":"Linear Add","cases":[{"case":"Dead","factor":1.4},{"case":"SDL","factor":1.4},{"case":"Live","factor":1.6}]}""");
            Ok(m, "define_combination", """{"name":"ENV","type":"Envelope","cases":[{"case":"ULS","factor":1}]}""");
            Ok(m, "define_load_pattern", """{"name":"Finishes","was":"SDL","type":"Super Dead","self_weight":0}""");
            Equal("Finishes", m.Model.AreaLoads[0].Pattern, "the floor load follows the pattern's new name");
            Check(m.Model.LoadCases.Exists(c => c.Name == "Finishes") && !m.Model.LoadCases.Exists(c => c.Name == "SDL"), "and its load case");
            var uls = m.Model.Combos.Find(c => c.Name == "ULS")!;
            Check(uls.Lines.Any(l => l.Contains("\"Finishes\"")), "and the combinations that use it");
            Check(m.Model.Combos.Find(c => c.Name == "ENV")!.Lines.Any(l => l[0] == "LOADCOMBO"), "a combination of combinations");
            Check(Error(m, "delete_definition", """{"kind":"load_pattern","name":"Finishes"}""").Contains("combinations"), "a pattern used by a combination is not deleted");
            Check(Error(m, "delete_definition", """{"kind":"combination","name":"ULS"}""").Contains("other combinations"), "a combination inside another is not deleted");
            Ok(m, "define_combination", """{"name":"ULS2","was":"ULS","type":"Linear Add","cases":[{"case":"Dead","factor":1.35},{"case":"Live","factor":1.5}]}""");
            Check(m.Model.Combos.Find(c => c.Name == "ENV")!.Lines.Any(l => l.Contains("\"ULS2\"")), "renaming a combination follows into the envelope");
            Ok(m, "delete_definition", """{"kind":"load_pattern","name":"Finishes"}""");
            Check(m.Model.AreaLoads.Count == 0 && !m.Model.LoadCases.Exists(c => c.Name == "Finishes"), "a deleted pattern takes its loads and case");
            Check(Error(m, "define_combination", """{"name":"X","cases":[{"case":"Nope","factor":1}]}""").Contains("Nope"), "a combination of an unknown case is refused");
            Check(Error(m, "define_combination", """{"name":"X","type":"Product","cases":[{"case":"Dead","factor":1}]}""").Contains("types"), "only the programs' combination types");
            var d = Ok(m, "model_definitions", "{}")["result"]!;
            var env = d["combinations"]!.AsArray().First(c => c!["name"]!.GetValue<string>() == "ENV")!;
            Equal("Envelope", env["type"]!.GetValue<string>(), "the combination's type comes back for its form");
            Equal("ULS2", env["cases"]![0]!["case"]!.GetValue<string>(), "and its cases");
            Check(d["pattern_types"]!.AsArray().Count > 10, "the pattern types for the form's list");
            // What is defined by hand reaches ETABS's text file.
            var text = CsiProMax.E2k.E2kWriter.Write(m.Model);
            Check(System.Text.RegularExpressions.Regex.IsMatch(text, "COMBO\\s+\"ENV\"\\s+LOADCOMBO\\s+\"ULS2\"") && System.Text.RegularExpressions.Regex.IsMatch(text, "COMBO\\s+\"ENV\"\\s+TYPE\\s+\"Envelope\""), "the combinations are in the .e2k");
        });

        yield return ("hand model: the Modeler's view has the Define, Draw, Assign and Edit menus and sends edits safely", () =>
        {
            var page = Pack.ViewerHtml();
            foreach (var part in new[] { "data-menu=\"Draw\"", "data-menu=\"Assign\"", "Define Load Combinations", "Load Combination Data", "Define Load Patterns", "Define Grid System Data", "Story Data",
                         "Frame Section Property Data", "Material Property Data", "Assign Joint Restraints", "Shell Load Assignment - Uniform", "New Model Quick Templates",
                         "Quick Draw Floors", "Properties of Object", "\"X-CPM-View\": \"1\"", "m:undo", "m:redo", "id=\"similar\"" })
                Check(page.Contains(part, StringComparison.Ordinal), "the page lacks " + part);
            // Every tool the page calls is one the Modeler runs.
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(page, "(?:hmRun|submit\\(d,)\\s*\\(?\"([a-z_0-9]+)\""))
            {
                var tool = call.Groups[1].Value;
                Check(tool is "undo" or "redo" || HandModel.Tools.ContainsKey(tool), "the page calls a tool the Modeler doesn't have: " + tool);
            }
            Check(!HandModel.Tools.ContainsKey("run_etabs_code") && !HandModel.Tools.ContainsKey("run_etabs_query_code"), "no code runs from the page");
        });

        yield return ("hand model: the edit address answers only its own page (secret, header, same origin)", () =>
        {
            var endpoint = new CsiProMax.Bridge.McpEndpoint("t", "1", () => "", _ => [], (_, _, _, _) => throw new InvalidOperationException());
            using var server = new CsiProMax.Bridge.McpHttpServer(endpoint);
            var seen = 0;
            server.ViewPost = (path, body) => { seen++; return Task.FromResult<(byte[], string)?>((Encoding.UTF8.GetBytes("{\"ok\":true}"), "application/json")); };
            server.Start();
            using var http = new HttpClient();
            HttpStatusCode Post(string url, string? origin, bool header)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                if (origin is not null) request.Headers.Add("Origin", origin);
                if (header) request.Headers.Add("X-CPM-View", "1");
                return http.Send(request).StatusCode;
            }
            var own = $"http://127.0.0.1:{server.Port}";
            Equal(HttpStatusCode.OK, Post(server.ViewUrl + "edit", own, true), "its own page");
            Equal(HttpStatusCode.Forbidden, Post(server.ViewUrl + "edit", "https://example.com", true), "another site");
            Equal(HttpStatusCode.NotFound, Post(server.ViewUrl + "edit", own, false), "without the header (a plain form)");
            Equal(HttpStatusCode.NotFound, Post($"{own}/view/wrong/edit", own, true), "without the secret");
            Equal(1, seen, "only the first reached the Modeler");
        });
    }
}
