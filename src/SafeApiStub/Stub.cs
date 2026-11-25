// The shapes of SAFE's plug-in types, as SAFE's API file (SAFEv1.dll) declares them: the same three as ETABS's, in
// the SAFEv1 namespace. Only their names and members matter: nothing of SAFE is in this file.
namespace SAFEv1
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
