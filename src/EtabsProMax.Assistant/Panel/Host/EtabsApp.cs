using EtabsProMax.Assistant.Etabs;

namespace EtabsProMax.ETABS;

/// <summary>
/// What a tool is handed when it runs: the ETABS this assistant is attached to. It stands where Revit's UIApplication
/// stands in Sketch ProMax, so the panel, the jobs and the approvals are the same code in both.
/// </summary>
public sealed class EtabsApp(IEtabsLink link)
{
    /// <summary>The attached ETABS (or the in-memory stand-in in a demo or a test).</summary>
    public IEtabsLink Link { get; } = link;

    /// <summary>The model open in ETABS now; null before ETABS answered. Named as in Sketch ProMax, so its code reads the same.</summary>
    public EtabsDoc? ActiveUIDocument => Host.ModelWatch.Current is { } m ? new EtabsDoc(m.Path, m.Title) : null;
}

/// <summary>The open model: its file ("" when never saved) and the title ETABS shows.</summary>
public sealed record EtabsDoc(string PathName, string Title)
{
    public EtabsDoc Document => this;
}

/// <summary>How a command ended, in the words Sketch ProMax's commands use.</summary>
public enum Result { Succeeded, Cancelled, Failed }
