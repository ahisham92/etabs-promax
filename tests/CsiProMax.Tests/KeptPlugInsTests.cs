using CsiProMax.Assistant;

/// <summary>
/// A scratch window leaves the plug-in lists as it found them: on the field-test PC (2026-11-25) SAP2000 26 and ETABS 23,
/// started through the API, rewrote their settings with NumberPlugIns=0, and Tools &gt; CSI ProMax was gone.
/// </summary>
static class KeptPlugInsTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    const string Before = "[Units]\r\nForce=kN\r\n\r\n[PlugIn]\r\n NumberPlugIns=1\r\n PlugInName=CsiProMaxSapPlugin.cPlugin\r\n PlugInMenuText=CSI ProMax\r\n PlugInPath=C:\\Users\\x\\Documents\\CSI ProMax\\CsiProMaxSapPlugin.dll\r\n\r\n[Recent]\r\nFile1=a.sdb\r\n";
    const string Emptied = "[Units]\r\nForce=kN\r\n\r\n[PlugIn]\r\n NumberPlugIns=0\r\n\r\n[Recent]\r\nFile1=b.sdb\r\n";

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("kept plug-ins: an emptied [PlugIn] section is put back, and nothing else in the file changes", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "cpm-kept-" + Guid.NewGuid().ToString("N"));
            try
            {
                var folder = Path.Combine(root, "SAP2000 26");
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(root, "ETABS 23"));
                var ini = Path.Combine(folder, "SAP2000v26.ini");
                File.WriteAllText(ini, Before, System.Text.Encoding.Latin1);
                File.WriteAllText(Path.Combine(root, "ETABS 23", "ETABS.ini"), Emptied, System.Text.Encoding.Latin1);
                var kept = KeptPlugIns.Take(CsiProgram.Sap2000, root);
                Check(kept.Count == 1 && kept.ContainsKey(ini), "only SAP2000's own settings are kept: " + string.Join(", ", kept.Keys));

                // Unchanged: nothing is written.
                Check(KeptPlugIns.PutBack(kept).Count == 0, "an unchanged list was written");

                // The scratch window empties the list and changes another section: only [PlugIn] comes back.
                File.WriteAllText(ini, Emptied, System.Text.Encoding.Latin1);
                var back = KeptPlugIns.PutBack(kept);
                var now = File.ReadAllText(ini, System.Text.Encoding.Latin1);
                Check(back.Count == 1, "the emptied list was not put back");
                Check(now.Contains(" NumberPlugIns=1\r\n PlugInName=CsiProMaxSapPlugin.cPlugin\r\n PlugInMenuText=CSI ProMax\r\n"), "the entry is not back: " + now);
                Check(now.Contains("File1=b.sdb") && !now.Contains("File1=a.sdb"), "another section was changed back too");
                Check(now.Contains("\r\n\r\n[Recent]") && !now.Contains("\n\n\n"), "the blank line between sections was lost or doubled: " + now.Replace("\r\n", "|"));
                Check(!now.Replace("\r\n", "").Contains('\n'), "the file's CRLF line ends were not kept");
                Check(File.ReadAllText(Path.Combine(root, "ETABS 23", "ETABS.ini"), System.Text.Encoding.Latin1) == Emptied, "another program's settings were touched");
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        });

        yield return ("kept plug-ins: the full test puts the lists back after each scratch window and says so", () =>
        {
            var root = SourceText.Root();
            var scratch = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "Field", "ScratchProgram.cs"));
            var take = scratch.IndexOf("_plugIns = KeptPlugIns.Take(_program);", StringComparison.Ordinal);
            var start = scratch.IndexOf("var before = Process.GetProcessesByName(_program.ProcessName)", StringComparison.Ordinal);
            Check(take > 0 && take < start, "the plug-in lists are not kept before the scratch window starts");
            var test = File.ReadAllText(Path.Combine(root, "src", "CsiProMax.Assistant", "FieldTest.cs"));
            Check(test.Contains("open.PutBackPlugIns()"), "the full test does not put the plug-in lists back after a scratch window");
        });
    }
}
