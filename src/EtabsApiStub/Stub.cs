using System.Runtime.InteropServices;

// The shapes of ETABS 23.2's plug-in types, as ETABSv1.dll declares them (read from the file on a PC with ETABS).
namespace ETABSv1
{
    [Guid("f0db2843-bbd6-4482-b859-00fbe390077f")]
    public interface cSapModel { }

    [Guid("496FB743-26AF-47C5-A713-C1B5CA85106C")]
    public interface cPluginCallback
    {
        void Finish(int iVal);
        int ErrorFlag { get; }
        bool Finished { get; }
    }

    [Guid("AB4B15AA-780A-4047-A814-3B1A83CBD26B")]
    public interface cPluginContract
    {
        void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin);
        int Info(ref string Text);
    }
}
