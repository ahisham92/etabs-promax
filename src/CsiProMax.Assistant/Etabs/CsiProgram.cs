using System.Diagnostics;

namespace CsiProMax.Assistant.Etabs;

/// <summary>
/// One of the CSI programs CSI ProMax works beside. They share one API shape (cOAPI, cSapModel and the same object
/// families), each in its own API file, so one link class drives them all and a program is a row here plus its own
/// plug-in entry and wording. ETABS is built and checked. SAP2000 and SAFE are listed so they plug in here; their
/// rows still have to be confirmed on a PC with that program, and they are not offered until then.
/// </summary>
public sealed record CsiProgram(string Key, string Name, string ProcessName, string ProgId, string ApiFile, string ApiNamespace, string UsualFolder, bool Ready)
{
    public static readonly CsiProgram Etabs = new("etabs", "ETABS", "ETABS", "CSI.ETABS.API.ETABSObject", "ETABSv1.dll", "ETABSv1", "ETABS 23", true);
    public static readonly CsiProgram Sap2000 = new("sap2000", "SAP2000", "SAP2000", "CSI.SAP2000.API.SapObject", "SAP2000v1.dll", "SAP2000v1", "SAP2000 26", true);
    // SAFE 20 and later: the same API shape as ETABS (its ProgID even says ETABSObject), in SAFEv1.dll, and in newer
    // versions also CSI's common CSiAPIv1.dll. Its own plug-in is CsiProMaxSafePlugin.dll (src/CsiProMax.Plugin.Safe).
    public static readonly CsiProgram Safe = new("safe", "SAFE", "SAFE", "CSI.SAFE.API.ETABSObject", "SAFEv1.dll", "SAFEv1", "SAFE 22", true);

    public static IReadOnlyList<CsiProgram> All { get; } = [Etabs, Sap2000, Safe];

    public static CsiProgram? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The program a process id belongs to, by its process name; ETABS when it can't be told.</summary>
    public static CsiProgram OfProcess(int? pid)
    {
        if (pid is not { } id) return Etabs;
        try
        {
            using var process = Process.GetProcessById(id);
            return All.FirstOrDefault(p => string.Equals(p.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase)) ?? Etabs;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Etabs; }
    }
}
