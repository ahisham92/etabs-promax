using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

// Kept to plain C# so the same file can be checked on its own against a running ETABS.
#pragma warning disable CS8600, CS8601, CS8610, CS8602, CS8603, CS8604, CS8618, CS8625, CS8765, CS8767

namespace CsiProMax.Assistant.Etabs
{
    /// <summary>
    /// One of ETABS's API objects, called by name. ETABS 23 can't be driven through plain COM late binding (asking the
    /// running ETABS for SapModel over IDispatch fails with "Specified cast is not valid"), so this goes through the
    /// API's own interfaces in ETABSv1.dll, loaded at run time from the ETABS install: still nothing of ETABS is needed
    /// to build. Numbers are turned into ETABS's enums on the way in and back into numbers on the way out.
    /// </summary>
    internal sealed class EtabsApi : DynamicObject
    {
        private readonly object _target;
        private readonly Type _type;

        public EtabsApi(object target, Type type)
        {
            _target = target;
            _type = type;
        }

        /// <summary>The API object itself, for the calls made by name (ApiCall).</summary>
        internal object Target { get { return _target; } }

        /// <summary>The API interface it is called through (cSapModel, cFrameObj, ...).</summary>
        internal Type ApiType { get { return _type; } }

        /// <summary>
        /// The API file of the running program (the one with this process id, when given), or of the program where it is
        /// usually installed. Each program has its own API file (ETABSv1.dll, SAP2000v1.dll, SAFEv1.dll); the newer
        /// versions also ship CSI's common API, CSiAPIv1.dll, which is used when the program's own file is not there.
        /// </summary>
        public static string FindApiFile(CsiProgram program, int? pid = null)
        {
            foreach (var process in Process.GetProcessesByName(program.ProcessName).OrderByDescending(p => p.Id == pid))
            {
                try
                {
                    var found = InFolder(Path.GetDirectoryName(process.MainModule.FileName), program);
                    if (found != null) return found;
                }
                catch (Exception) { }
            }
            var csi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Computers and Structures");
            var usual = InFolder(Path.Combine(csi, program.UsualFolder), program);
            if (usual != null) return usual;
            // Another version than the usual one: the newest folder named after the program ("SAFE 21", "SAFE 22").
            try
            {
                foreach (var folder in Directory.GetDirectories(csi, program.Name + "*").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
                    if (InFolder(folder, program) is string other) return other;
            }
            catch (Exception) { }
            return Path.Combine(csi, program.UsualFolder, program.ApiFile);
        }

        /// <summary>The program's API file in one folder, else CSI's common API file there, else null.</summary>
        private static string InFolder(string folder, CsiProgram program)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            var own = Path.Combine(folder, program.ApiFile);
            if (File.Exists(own)) return own;
            var common = Path.Combine(folder, CommonApiFile);
            return File.Exists(common) ? common : null;
        }

        /// <summary>CSI's common API for ETABS, SAP2000, SAFE and CSiBridge (newer versions ship it beside their own).</summary>
        public const string CommonApiFile = "CSiAPIv1.dll";

        /// <summary>The namespace of an API file's types: the file's own name (ETABSv1, SAP2000v1, SAFEv1, CSiAPIv1).</summary>
        public static string NamespaceOf(string apiFile) => Path.GetFileNameWithoutExtension(apiFile);

        [DllImport("ole32.dll")]
        private static extern int CLSIDFromProgID([MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

        [DllImport("oleaut32.dll")]
        private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

        /// <summary>
        /// The running ETABS (its cOAPI object), or null when it isn't open. With a process id, that ETABS window and no
        /// other (the API's own Helper.GetObjectProcess): several ETABS windows can be open, and the assistant must work
        /// on the one it was started from. Without one, the ETABS that is the "active instance for API".
        /// </summary>
        public static EtabsApi Attach(string apiFile, CsiProgram program, int? pid = null)
        {
            var api = Assembly.LoadFrom(apiFile);
            var ns = NamespaceOf(apiFile);
            var type = api.GetType(ns + ".cOAPI", true);
            var progId = program.ProgId;
            try
            {
                if (pid == null && CLSIDFromProgID(progId, out var clsid) == 0 && GetActiveObject(ref clsid, IntPtr.Zero, out var running) == 0 && running != null)
                    return new EtabsApi(running, type);
                var helper = Activator.CreateInstance(api.GetType(ns + ".Helper", true));
                var helperType = api.GetType(ns + ".cHelper", true);
                // Older APIs (SAP2000 before 23, say) have no GetObjectProcess: then the active instance is the one.
                var byProcess = pid == null ? null : helperType.GetMethod("GetObjectProcess");
                var app = byProcess == null
                    ? helperType.GetMethod("GetObject").Invoke(helper, new object[] { progId })
                    : byProcess.Invoke(helper, new object[] { progId, pid.Value });
                return app == null ? null : new EtabsApi(app, type);
            }
            catch (Exception ex) when (ex is TargetInvocationException || ex is MissingMethodException || ex is COMException || ex is NullReferenceException) { return null; }
        }

        /// <summary>
        /// A property of one of the program's objects: read here, or over the pipe for a program on the .NET Framework
        /// (<see cref="ApiPipe"/>). Every read of the API goes through this or <see cref="Invoke"/>.
        /// </summary>
        internal static object GetValue(PropertyInfo property, object target) =>
            target is ApiPipe.Remote remote ? remote.Pipe.Get(remote, property) : property.GetValue(target, null);

        /// <summary>A method of one of the program's objects, as MethodInfo.Invoke calls it (ref values come back in <paramref name="values"/>).</summary>
        internal static object Invoke(MethodInfo method, object target, object[] values) =>
            target is ApiPipe.Remote remote ? remote.Pipe.Call(remote, method, values) : method.Invoke(target, values);

        /// <summary>
        /// True for an API file built for the .NET Framework (it names mscorlib): the API of ETABS 21, SAP2000 25 and
        /// SAFE 21 and older. Those reach the program through .NET Remoting, which .NET 8 does not have, so CSI ProMax
        /// calls them through the plug-in's pipe (<see cref="ApiPipe"/>), never through their Helper.
        /// </summary>
        public static bool RunsOnFramework(string apiFile)
        {
            try
            {
                using (var stream = File.OpenRead(apiFile))
                using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
                {
                    var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                    var names = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
                    return names.Contains("mscorlib") && !names.Contains("System.Runtime") && !names.Contains("netstandard");
                }
            }
            catch (Exception) { return false; }
        }

        private static object Wrap(object value, Type declared)
        {
            if (value == null) return null;
            return declared.IsInterface && declared.Namespace is "ETABSv1" or "SAP2000v1" or "SAFEv1" or "CSiAPIv1" ? new EtabsApi(value, declared) : value;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            var property = _type.GetProperty(binder.Name);
            if (property == null) throw new MissingMemberException("The program's " + _type.Name + " has no " + binder.Name + ".");
            result = Wrap(Call(() => GetValue(property, _target)), property.PropertyType);
            return true;
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            foreach (var method in _type.GetMethods().Where(m => m.Name == binder.Name))
            {
                var parameters = method.GetParameters();
                if (args.Length > parameters.Length) continue;
                var values = new object[parameters.Length];
                var fits = true;
                for (var i = 0; i < parameters.Length && fits; i++)
                {
                    if (i < args.Length) fits = TryConvert(args[i], parameters[i].ParameterType, out values[i]);
                    else if (!parameters[i].IsOptional) fits = false;
                    else if (parameters[i].DefaultValue == null || parameters[i].DefaultValue is DBNull) values[i] = Type.Missing;
                    else fits = TryConvert(parameters[i].DefaultValue, parameters[i].ParameterType, out values[i]);
                }
                if (!fits) continue;
                var value = Call(() => Invoke(method, _target, values));
                for (var i = 0; i < args.Length; i++)
                    if (parameters[i].ParameterType.IsByRef) args[i] = Back(values[i], args[i]);
                result = Wrap(value, method.ReturnType);
                return true;
            }
            // Name what the program does have, so the next field test report says which shape to call.
            var shapes = _type.GetMethods().Where(m => m.Name == binder.Name)
                .Select(m => binder.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name.Replace("&", " ref") + " " + p.Name)) + ")").ToList();
            throw new MissingMethodException("The program's " + _type.Name + " has no " + binder.Name + " that takes these " + args.Length + " values." +
                (shapes.Count > 0 ? " It has: " + string.Join("; ", shapes) + "." : ""));
        }

        private static object Call(Func<object> body)
        {
            try { return body(); }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                throw;
            }
        }

        private static bool TryConvert(object value, Type type, out object converted)
        {
            var target = type.IsByRef ? type.GetElementType() : type;
            converted = value;
            if (value == null) return !target.IsValueType;
            if (target.IsInstanceOfType(value)) return true;
            if (target.IsEnum && IsWhole(value)) { converted = Enum.ToObject(target, value); return true; }
            if (target == typeof(double) && (IsWhole(value) || value is float)) { converted = Convert.ToDouble(value); return true; }
            return false;
        }

        private static bool IsWhole(object value)
        {
            return value is int || value is short || value is long || value is byte;
        }

        private static object Back(object value, object original)
        {
            if (value != null && value.GetType().IsEnum && original is int) return Convert.ToInt32(value);
            return value;
        }
    }
}
