using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

// Kept to plain C# so the same file can be checked on its own against a running ETABS.
#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8618, CS8625, CS8765, CS8767

namespace EtabsProMax.Assistant.Etabs
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

        /// <summary>The API file of the running ETABS, or of ETABS 23 where it is usually installed.</summary>
        public static string FindApiFile()
        {
            foreach (var process in Process.GetProcessesByName("ETABS"))
            {
                try
                {
                    var file = Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "ETABSv1.dll");
                    if (File.Exists(file)) return file;
                }
                catch (Exception) { }
            }
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programs, "Computers and Structures", "ETABS 23", "ETABSv1.dll");
        }

        /// <summary>The running ETABS (its cOAPI object), or null when ETABS isn't open.</summary>
        public static EtabsApi Attach(string apiFile, string progId)
        {
            var api = Assembly.LoadFrom(apiFile);
            var helperType = api.GetType("ETABSv1.Helper", true);
            var helper = Activator.CreateInstance(helperType);
            object app;
            try { app = api.GetType("ETABSv1.cHelper", true).GetMethod("GetObject").Invoke(helper, new object[] { progId }); }
            catch (TargetInvocationException) { return null; }
            return app == null ? null : new EtabsApi(app, api.GetType("ETABSv1.cOAPI", true));
        }

        private static object Wrap(object value, Type declared)
        {
            if (value == null) return null;
            return declared.IsInterface && declared.Namespace == "ETABSv1" ? new EtabsApi(value, declared) : value;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            var property = _type.GetProperty(binder.Name);
            if (property == null) throw new MissingMemberException("ETABS's " + _type.Name + " has no " + binder.Name + ".");
            result = Wrap(Call(() => property.GetValue(_target, null)), property.PropertyType);
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
                var value = Call(() => method.Invoke(_target, values));
                for (var i = 0; i < args.Length; i++)
                    if (parameters[i].ParameterType.IsByRef) args[i] = Back(values[i], args[i]);
                result = Wrap(value, method.ReturnType);
                return true;
            }
            throw new MissingMethodException("ETABS's " + _type.Name + " has no " + binder.Name + " that takes these " + args.Length + " values.");
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
