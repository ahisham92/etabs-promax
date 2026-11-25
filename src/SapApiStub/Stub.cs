// The shapes of SAP2000's plug-in types, as SAP2000v1.dll declares them: the same as ETABS's (cSapModel,
// cPluginCallback and cPluginContract), in SAP2000's own namespace.
namespace SAP2000v1
{
    public interface cSapModel { }

    public interface cPluginCallback
    {
        void Finish(int iVal);
        int ErrorFlag { get; }
        bool Finished { get; }
    }

    public interface cPluginContract
    {
        void Main(ref cSapModel SapModel, ref cPluginCallback ISapPlugin);
        int Info(ref string Text);
    }
}
