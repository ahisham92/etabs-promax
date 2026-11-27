using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;
using CsiProMax.Assistant.Tools;
using CsiProMax.Model;

/// <summary>
/// ETABS 23.2's File menu in the Engine (Ahmed 2026-11-27: every form identical, every button working as in ETABS):
/// the menu item for item as pictured, and the Export items writing their files without touching the open model.
/// </summary>
static class EtabsFileMenuTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    static JsonObject Run(ICsiLink link, string tool, object args) =>
        (JsonObject)FileMenuTools.All.Single(x => x.Name == tool).Run(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, link);

    static string Folder() => Path.Combine(Path.GetTempPath(), "cpm-file-" + Guid.NewGuid().ToString("N")[..8]);

    static string Viewer() => File.ReadAllText(Path.Combine(SourceText.Root(), "src", "CsiProMax.Viewer", "viewer.html"));

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("ETABS File menu: its items in ETABS 23.2's order, with its shortcuts", () =>
        {
            var v = Viewer();
            var at = v.IndexOf("function etfFileMenu()", StringComparison.Ordinal);
            Check(at > 0, "no etfFileMenu");
            var menu = v[at..v.IndexOf("\n}\n", at, StringComparison.Ordinal)];
            var order = new[] { "\"New Model...\", \"m:new\", \"Ctrl+N\"", "\"Open...\", \"m:open\", \"Ctrl+O\"", "\"Close...\", \"m:etf:close\", \"Shift+Ctrl+E\"", "\"Save\", \"m:save\", \"Ctrl+S\"",
                "\"Save As...\", \"m:saveAs\", \"Shift+Ctrl+S\"", "[\"Import\"", "[\"Export\"", "\"ETABS .e2k Text File...\"", "\"ETABS Database Tables to Excel...\"", "\"ETABS Database Tables to Text...\"",
                "\"ETABS Database Tables to XML...\"", "\"Story as SAFE V12 .f2k File...\"", "\".DXF/.DWG File...\"", "\"IFC File...\"", "\"IGES .igs File...\"", "\"3D Printing .stl File...\"",
                "\"3D Image .obj File...\"", "\"Perform3D Text File...\"", "\"Perform3D Structure File...\"", "\"Create Video...\"", "\"Print Graphics...\", \"m:etf:print\", \"Ctrl+P\"", "\"Exit\", \"m:etf:exit\"", "\"Engine Extras\"" };
            var last = -1;
            foreach (var item in order)
            {
                var i = menu.IndexOf(item, StringComparison.Ordinal);
                Check(i > last, item + (i < 0 ? " is not in the File menu" : " is out of ETABS's order"));
                last = i;
            }
            Check(v.Contains("if (etnOn()) m.File = etfFileMenu();", StringComparison.Ordinal), "the ETABS File menu is not the one shown for ETABS");
            Check(v.Contains("\"ETABS\", `<p style=\"margin:4px 8px 2px\">${esc(etfModelName())} has changed.</p><p style=\"margin:2px 8px 10px\">Save current changes?</p>`", StringComparison.Ordinal) &&
                v.Contains("[[\"Yes\", () => { hmSave(false)", StringComparison.Ordinal), "the save box is not ETABS's");
            Check(v.Contains("\"Export ETABS Model Text File As\", [[\"ETABS Model Files (*.e2k)\", \"e2k\"]]", StringComparison.Ordinal), "the e2k Save As is not ETABS's");
            Check(v.Contains("dialog(\"Consistent Units\"", StringComparison.Ordinal) && v.Contains("Length Unit</td>", StringComparison.Ordinal) && v.Contains("Force Unit</td>", StringComparison.Ordinal) && v.Contains("Temperature Unit</td>", StringComparison.Ordinal), "the Consistent Units form");
            foreach (var tip in new[] { "title=\"Print\"", "title=\"Printer Setup\"", "title=\"Page Setup\"", "title=\"Add Image\"", ">Preview...</button>", "<b>Drawing Scale</b>", "<b>Zoom</b>" })
                Check(v.Contains(tip, StringComparison.Ordinal), "Print Graphics lacks " + tip);
            Check(v.Contains("title: \"Open Model\" + (program === \"etabs\" ? \" File\" : \"\")", StringComparison.Ordinal) && v.Contains("title: \"Save Model As\".replace(\"Model\", k === \"etabs\" ? \"Model File\" : \"Model\")", StringComparison.Ordinal), "Open and Save As are not titled as ETABS's");
        });

        yield return ("ETABS File > Export > .e2k: written in the Consistent Units form's units, the open model unchanged", () =>
        {
            var folder = Folder();
            try
            {
                var link = new OfflineEtabs(Templates.ConcreteFrame("Frame", [6, 5], [4], 2, 3.2));
                var path = Path.Combine(folder, "out.e2k");
                var r = Run(link, "export_e2k", new { path, length = "mm", force = "N", temperature = "C" });
                Check(File.Exists(path), "no file");
                var text = File.ReadAllText(path);
                Check(text.Contains("UNITS  \"N\"  \"mm\"", StringComparison.OrdinalIgnoreCase) || text.Contains("UNITS \"N\" \"mm\"", StringComparison.OrdinalIgnoreCase), "the file is not in N, mm: " + text.Split('\n').FirstOrDefault(l => l.Contains("UNITS")));
                var back = OfflineEtabs.Load(path);
                Check(Math.Abs(back.Stories.Sum(s => s.Height) - 6400) < 1e-6, "the stories read back " + back.Stories.Sum(s => s.Height) + " mm high, not 6400");
                Check(link.Model.Units.Length.Equals("m", StringComparison.OrdinalIgnoreCase) && link.Path.Length == 0, "the open model changed its units or file");
                Check(r["saved"]!.GetValue<string>() == path, "the answer does not name the file");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });

        yield return ("ETABS File > Export > Database Tables to Text and XML: every table picked, read back", () =>
        {
            var folder = Folder();
            try
            {
                var link = new OfflineEtabs(Templates.ConcreteFrame("Frame", [6], [4], 1, 3));
                var txt = Path.Combine(folder, "t.txt");
                Run(link, "export_tables_file", new { path = txt, format = "text" });
                var tables = CsiProMax.CsiTables.CsiTableText.Parse(File.ReadAllText(txt));
                Check(tables.Count > 3, "only " + tables.Count + " tables in the text file");
                Check(File.ReadAllText(txt).TrimEnd().EndsWith("END TABLE DATA", StringComparison.Ordinal), "no END TABLE DATA");
                var xml = Path.Combine(folder, "t.xml");
                var r = Run(link, "export_tables_file", new { path = xml, format = "xml" });
                var doc = System.Xml.Linq.XDocument.Load(xml);
                Check(doc.Root!.Elements("Table").Count() == r["tables"]!.AsArray().Count, "the XML does not hold every table");
                Check(doc.Root.Elements("Table").Sum(t => t.Elements("Row").Count()) == r["tables"]!.AsArray().Sum(t => t!["rows"]!.GetValue<int>()), "the XML does not hold every row");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });

        yield return ("ETABS File > Export geometry: only the export file types are written", () =>
        {
            var folder = Folder();
            try
            {
                var link = new OfflineEtabs();
                var ok = Path.Combine(folder, "m.ifc");
                Run(link, "write_export_file", new { path = ok, text = "ISO-10303-21;" });
                Check(File.ReadAllText(ok) == "ISO-10303-21;", "the IFC file was not written as given");
                var refused = false;
                try { Run(link, "write_export_file", new { path = Path.Combine(folder, "m.exe"), text = "x" }); }
                catch (ToolInputException) { refused = true; }
                Check(refused && !File.Exists(Path.Combine(folder, "m.exe")), "a .exe was written");
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        });
    }
}
