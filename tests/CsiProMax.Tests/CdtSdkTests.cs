/// <summary>
/// The CDT SDK (a private repository) inside CSI ProMax, as in Sketch ProMax: its compiled DLLs in lib/cdt-sdk (built
/// from the commit in cdt-sdk.ref) shipped beside CsiProMax.dll, and every panel window themed by it through Brand
/// (Panel/UI/CdtTheme.cs). No SDK source is committed to this public repository.
/// </summary>
static class CdtSdkTests
{
    static void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static IEnumerable<(string Name, Action Body)> All()
    {
        var root = SourceText.Root();
        var ui = Path.Combine(root, "src", "CsiProMax.Assistant", "Panel", "UI");

        yield return ("cdt sdk: the build pins one SDK commit (cdt-sdk.ref)", () =>
        {
            var file = Path.Combine(root, "cdt-sdk.ref");
            var pin = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            Check(System.Text.RegularExpressions.Regex.IsMatch(pin, "^[0-9a-f]{40}$"), "cdt-sdk.ref holds no commit: " + pin);
        });

        yield return ("cdt sdk: its compiled DLLs are in lib/cdt-sdk, and nothing else of it", () =>
        {
            var lib = Path.Combine(root, "lib", "cdt-sdk");
            var missing = new[] { "CDT.Core.dll", "CDT.Wpf.dll" }.Where(d => !File.Exists(Path.Combine(lib, "net8.0-windows", d))).ToList();
            Check(missing.Count == 0, "missing from lib/cdt-sdk/net8.0-windows: " + string.Join(", ", missing));
            var source = Directory.GetFiles(lib, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
            Check(source.Count == 0, "only the SDK's DLLs may be committed (the repository is public): " + string.Join(", ", source));
        });

        yield return ("cdt sdk: the release build needs no secret or private checkout, and ships the SDK's DLLs", () =>
        {
            var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
            Check(!release.Contains("CDT_SDK_TOKEN") && !release.Contains("/CDT-SDK"), "release.yml reaches for the private SDK");
            foreach (var dll in new[] { "CDT.Core.dll", "CDT.Wpf.dll", "WpfMath.dll" })
                Check(release.Contains("package/dist/" + dll), "release.yml does not check that the package holds " + dll);
        });

        yield return ("cdt sdk: every themed window gets its style sheet, palette, title bar and focus assist", () =>
        {
            var brand = File.ReadAllText(Path.Combine(ui, "Brand.cs"));
            foreach (var part in new[] { "CdtTheme.Styles", "CdtTheme.WritePalette", "CdtTheme.TitleBar", "CdtTheme.Focus", "\"CDT.PrimaryButton\"", "\"CDT.TextBox\"" })
                Check(brand.Contains(part), "Brand.cs lacks " + part);
        });

        yield return ("cdt sdk: the panel's message boxes are the SDK's (UI.MessageBox), never the plain Windows one", () =>
        {
            var plain = SourceText.Files("src/CsiProMax.Assistant/Panel").Where(f => f.Text.Contains("System.Windows.MessageBox.Show")).Select(f => f.Path).ToList();
            Check(plain.Count == 0, "plain message boxes in " + string.Join(", ", plain));
        });
    }
}
