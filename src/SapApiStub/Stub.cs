// The shapes of SAP2000's plug-in types, as SAP2000v1.dll declares them: the model and the callback a plug-in's Main is
// given. Only their names and members matter: nothing of SAP2000 is in this file.
//
// There is no cPluginContract here, on purpose. SAP2000v1.dll has that interface only from SAP2000 26 on (the versions that run
// on .NET 8); the older versions, which run on the .NET Framework, have none (read from the API file of ETABS 18:
// 279 types, cPluginCallback among them, no cPluginContract). A plug-in class that names it cannot be created in the
// older versions at all, so the stand-in does not offer it and such a class does not compile.
namespace SAP2000v1
{
    public interface cSapModel { }

    public interface cPluginCallback
    {
        void Finish(int iVal);
        int ErrorFlag { get; }
        bool Finished { get; }
    }
}
