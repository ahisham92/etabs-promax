using System.Runtime.InteropServices;

// The shapes of ETABS's plug-in types, as ETABSv1.dll declares them: the model and the callback a plug-in's Main is
// given. Only their names and members matter: nothing of ETABS is in this file.
//
// There is no cPluginContract here, on purpose. ETABSv1.dll has that interface only from ETABS 22 on (the versions that run
// on .NET 8); the older versions, which run on the .NET Framework, have none (read from the API file of ETABS 18:
// 279 types, cPluginCallback among them, no cPluginContract). A plug-in class that names it cannot be created in the
// older versions at all, so the stand-in does not offer it and such a class does not compile.
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
}
