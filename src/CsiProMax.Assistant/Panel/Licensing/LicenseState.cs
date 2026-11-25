using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CsiProMax.Licensing;

/// <summary>
/// Whether this PC may use CSI ProMax: the owner's PCs always may; any other PC needs a key made for its PC code
/// (UI/LicenseWindow). Files live in %APPDATA%\CsiProMax\license: key.txt (this PC's key), changes.txt (the owner's
/// later changes, last read online), and on the owner's PC signing.dat (the signing secret, protected by Windows for
/// this user) and issued.json (the License manager's list). TritoETABS's shared tools are not licensed here.
/// </summary>
internal static class LicenseState
{
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CsiProMax", "license");
    private static string KeyFile => Path.Combine(Folder, "key.txt");
    private static string ChangesFile => Path.Combine(Folder, "changes.txt");
    /// <summary>The changes to keys from Sketch ProMax's License manager, published in that repository.</summary>
    private static string SharedChangesFile => Path.Combine(Folder, "changes-shared.txt");
    /// <summary>Where Sketch ProMax and Plaxis ProMax keep the key pasted there: a key for every app works here without pasting it again.</summary>
    private static string[] OtherKeyFiles =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "license", "key.txt"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlaxisProMax", "license", "key.txt"),
    ];
    internal const string SharedChangesUrl = "https://raw.githubusercontent.com/ahisham92/sketch-promax/main/licenses/changes.txt";
    private static string SigningFile => Path.Combine(Folder, "signing.dat");
    /// <summary>A secret dropped here in plain text is protected into signing.dat the first time the manager opens.</summary>
    private static string PlainSigningFile => Path.Combine(Folder, "signing-secret.txt");

    /// <summary>Where the owner's changes are published (licenses/changes.txt on main).</summary>
    internal const string ChangesUrl = "https://raw.githubusercontent.com/ahisham92/etabs-promax/main/licenses/changes.txt";

    internal enum Kind { Owner, Licensed, Locked }

    internal sealed record Status(Kind Kind, string PcCode, LicenseKey.License? License, LicenseKey.Check Check, DateTime? Ends)
    {
        public bool Allowed => Kind != Kind.Locked;
    }

    private static Status? _current;
    private static readonly object Gate = new();

    public static Status Current
    {
        get
        {
            lock (Gate) return _current ??= Evaluate();
        }
    }

    public static bool Allowed => Current.Allowed;

    public static bool IsOwnerPc => Current.Kind == Kind.Owner;

    /// <summary>Reads everything again (after a key is entered or the changes list arrives).</summary>
    public static Status Refresh()
    {
        lock (Gate) return _current = Evaluate();
    }

    private static Status Evaluate()
    {
        var id = MachineId();
        // The code shown is Sketch ProMax's, which the one License manager makes keys for; CSI ProMax's own keys
        // (made for its CPM- code) still work.
        var shown = LicenseKey.SketchPcCode(id);
        if (LicenseKey.IsOwner(id)) return new Status(Kind.Owner, shown, null, LicenseKey.Check.Valid, null);
        (LicenseKey.License? License, LicenseKey.Check Check, DateTime? Ends) Try(string? key)
        {
            var license = LicenseKey.Read(key);
            var changes = LicenseChanges.Parse(ReadText(license?.Shared == true ? SharedChangesFile : ChangesFile));
            var code = license?.Shared == true ? shown : LicenseKey.PcCode(id);
            var check = code.Length == 0 ? LicenseKey.Check.WrongPc : LicenseKey.Verify(license, code, DateTime.Today, changes);
            return (license, check, license is null ? null : LicenseKey.EndsOf(license, changes));
        }
        var best = Try(ReadText(KeyFile));
        if (best.Check != LicenseKey.Check.Valid)
            foreach (var other in OtherKeyFiles)
                if (ReadText(other) is { } text && Try(text) is { Check: LicenseKey.Check.Valid } found) { best = found; break; }
        return new Status(best.Check == LicenseKey.Check.Valid ? Kind.Licensed : Kind.Locked, shown, best.License, best.Check, best.Ends);
    }

    /// <summary>Checks <paramref name="key"/> for this PC; saves it and unlocks when it is good. Returns the check.</summary>
    public static LicenseKey.Check Activate(string key)
    {
        var license = LicenseKey.Read(key);
        var id = MachineId();
        var code = license?.Shared == true ? LicenseKey.SketchPcCode(id) : LicenseKey.PcCode(id);
        var changes = LicenseChanges.Parse(ReadText(license?.Shared == true ? SharedChangesFile : ChangesFile));
        var check = LicenseKey.Verify(license, code, DateTime.Today, changes);
        if (check == LicenseKey.Check.Valid)
        {
            Directory.CreateDirectory(Folder);
            SafeFile.Write(KeyFile, new string(key.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            Refresh();
        }
        return check;
    }

    /// <summary>What a check means, in the user's words.</summary>
    public static string Explain(LicenseKey.Check check, DateTime? ends = null) => check switch
    {
        LicenseKey.Check.Valid => "The key is valid for this PC.",
        LicenseKey.Check.WrongPc => "This key belongs to another PC. Send your PC code (above) to whoever gave you CSI ProMax to get a key for this PC.",
        LicenseKey.Check.Expired => ends is { } e ? $"This key ended on {e:d MMM yyyy}. Ask for a new key." : "This key has ended. Ask for a new key.",
        LicenseKey.Check.OtherProgram => "This key is for Sketch ProMax or Plaxis ProMax, not CSI ProMax. Ask whoever gave it to you for a key that includes CSI ProMax (your PC code stays the same).",
        LicenseKey.Check.Cancelled => "This key was cancelled. Ask whoever gave you CSI ProMax for a new key.",
        _ => "That isn't a CSI ProMax key. Paste the whole key, starting with SPM1. or CPM1.",
    };

    /// <summary>The Windows machine ID (HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid), or "" when it can't be read.</summary>
    internal static string MachineId()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    /// <summary>
    /// Reads the owner's changes online and keeps them for offline days; a key cancelled or shortened there stops working
    /// at once. Owner PCs skip it. Silent on failure: the last list read stays in force.
    /// </summary>
    public static async Task RefreshChangesAsync(CancellationToken ct)
    {
        if (IsOwnerPc) return;
        try
        {
#if NETFRAMEWORK
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var changed = false;
            foreach (var (url, file) in new[] { (ChangesUrl, ChangesFile), (SharedChangesUrl, SharedChangesFile) })
            {
                using var response = await http.GetAsync(url + "?t=" + DateTime.UtcNow.Ticks, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (text.Length > 1_000_000 || text == ReadText(file)) continue;
                Directory.CreateDirectory(Folder);
                SafeFile.Write(file, text);
                changed = true;
            }
            if (changed) Refresh();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { }
    }

    // ---- The owner's signing secret (License manager only)

    /// <summary>The signing secret on this PC, or null. A plain signing-secret.txt is protected into signing.dat and deleted.</summary>
    public static string? SigningSecret()
    {
        try
        {
            var plain = ReadText(PlainSigningFile)?.Trim();
            if (plain is not null && LicenseKey.MatchesBuiltIn(plain))
            {
                SaveSigningSecret(plain);
                File.Delete(PlainSigningFile);
                return plain;
            }
            // The owner signs CSI ProMax keys with the same secret as Sketch ProMax keys: when it is already on this
            // PC for Sketch ProMax, it is used from there, so it isn't pasted twice.
            var sketch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SketchProMax", "license", "signing.dat");
            var file = File.Exists(SigningFile) ? SigningFile : File.Exists(sketch) ? sketch : null;
            if (file is null) return null;
            var secret = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(file)));
            return LicenseKey.MatchesBuiltIn(secret) ? secret : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Keeps <paramref name="secret"/> on this PC, protected by Windows so only this Windows user can read it.</summary>
    public static void SaveSigningSecret(string secret)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(SigningFile, Dpapi.Protect(Encoding.UTF8.GetBytes(secret.Trim())));
    }

    private static string? ReadText(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Windows data protection for the current user (crypt32), without an extra package.</summary>
    internal static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private const int UiForbidden = 0x1;

        public static byte[] Protect(byte[] data) => Run(data, protect: true);

        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, "CSI ProMax", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { handle.Free(); }
        }
    }
}
