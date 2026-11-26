using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsiProMax.Assistant.Etabs;

namespace CsiProMax.Assistant.Tools;

/// <summary>
/// Calls any method of the program's API by its path from the model ("FrameObj.GetNameList",
/// "LoadPatterns.AutoSeismic.SetUserCoefficient") with its arguments given by parameter name, and gives back the
/// return code and every ref or out value by name. The API is the same shape in ETABS, SAP2000 and SAFE, but a few
/// methods take their arguments in another order or under other names from one program (or version) to the next:
/// giving them by name, with the ones left out set to their defaults, lets one tool drive all three.
///
/// The search and describe parts walk the API's own interfaces from cSapModel, so the chat can find a method the
/// dedicated tools do not cover and see its parameters before it calls it. The idea of a generic layer over the
/// whole API (search, describe, call) is adapted from the etabs-mcp project by Moha2005269 (Apache-2.0).
/// </summary>
public static class ApiCall
{
    /// <summary>One method of the API, reached from the model by a path of properties.</summary>
    public sealed record Member(string Path, MethodInfo Method)
    {
        public string Signature => $"{Path}({string.Join(", ", Method.GetParameters().Select(Describe))}) -> {Name(Method.ReturnType)}";
    }

    /// <summary>The object and the type it is called through: the API wrapper's interface, or the object's own type.</summary>
    internal static (object Target, Type Type) Unwrap(object model) =>
        model is EtabsApi api ? (api.Target, api.ApiType) : (model, model.GetType());

    /// <summary>True for a method that only reads: Get..., Count..., and the few other reading calls.</summary>
    public static bool Reads(string method)
    {
        var name = method.Contains('.') ? method[(method.LastIndexOf('.') + 1)..] : method;
        return name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Count", StringComparison.Ordinal)
            || name is "GetNameList" or "GetModelIsLocked" or "GetPresentUnits" or "GetVersion" or "GetModelFilename" or "GetModelFilepath";
    }

    /// <summary>
    /// Calls one method by path. <paramref name="named"/> gives arguments by parameter name (any case); a parameter
    /// left out takes its default (0, false, "", an empty list). Throws <see cref="ToolInputException"/> when the path
    /// is wrong, and <see cref="MissingMethodException"/> when this API has no such method.
    /// </summary>
    public static JsonObject Invoke(object model, string path, IReadOnlyDictionary<string, object?>? named = null, IReadOnlyList<object?>? positional = null)
    {
        var (target, type, method, where) = Resolve(model, path, named);
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            object? given = null;
            var has = false;
            if (named is not null && Find(named, p.Name ?? "") is { } key) { given = named[key]; has = true; }
            else if (positional is not null && i < positional.Count) { given = positional[i]; has = true; }
            values[i] = has ? Convert(given, t, p.Name ?? $"argument {i + 1}") : p.HasDefaultValue && p.DefaultValue is not DBNull && p.DefaultValue is not null ? Convert(p.DefaultValue, t, p.Name ?? "") : Default(t);
        }
        object? answer;
        try { answer = EtabsApi.Invoke(method, target, values!); }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw new EtabsException($"The program's {where}.{method.Name} stopped with an error: {inner.Message}");
        }
        var result = new JsonObject { ["method"] = $"{where}.{method.Name}" };
        if (method.ReturnType != typeof(void)) result["return"] = ToJson(answer);
        var outs = new JsonObject();
        for (var i = 0; i < parameters.Length; i++)
            if (parameters[i].ParameterType.IsByRef) outs[parameters[i].Name ?? $"arg{i + 1}"] = ToJson(values[i]);
        if (outs.Count > 0) result["values"] = outs;
        return result;
    }

    /// <summary>True when the method's answer is the API's "it worked" (0, or a method that returns nothing).</summary>
    public static bool Worked(JsonObject call) => call["return"] is not JsonValue v || v.TryGetValue<int>(out var code) && code == 0 || v.TryGetValue<long>(out var l) && l == 0;

    /// <summary>A ref or out value of a call, by its parameter name in any case.</summary>
    public static JsonNode? Value(JsonObject call, string name) =>
        call["values"] is JsonObject values && values.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : null;

    /// <summary>The same as a number (NaN when it is not one).</summary>
    public static double Number(JsonObject call, string name) => Number(Value(call, name));

    /// <summary>A JSON value as a number, whether it holds a whole number or not (NaN when it is not one).</summary>
    public static double Number(JsonNode? node) =>
        node is not JsonValue v ? double.NaN : v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l : double.NaN;

    /// <summary>Like <see cref="Invoke"/>, and throws when the program answers anything but 0.</summary>
    public static JsonObject Must(object model, string path, IReadOnlyDictionary<string, object?>? named, string what)
    {
        var call = Invoke(model, path, named);
        if (!Worked(call)) throw new EtabsException($"The program refused to {what} ({call["method"]} answered {call["return"]}).");
        return call;
    }

    /// <summary>The first of several paths this API has (they differ between programs and versions), called.</summary>
    public static JsonObject First(object model, IEnumerable<string> paths, IReadOnlyDictionary<string, object?>? named, string what)
    {
        var tried = new List<string>();
        foreach (var path in paths)
        {
            try { return Must(model, path, named, what); }
            catch (Exception ex) when (ex is MissingMethodException or ToolInputException or EtabsException) { tried.Add($"{path}: {ex.Message}"); }
        }
        throw new EtabsException($"The program could not {what}. Tried: " + string.Join(" | ", tried));
    }

    private static (object Target, Type Type, MethodInfo Method, string Where) Resolve(object model, string path, IReadOnlyDictionary<string, object?>? named)
    {
        var parts = path.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ToolInputException("Give the method as a path from the model, e.g. FrameObj.GetNameList.");
        if (parts[0] is "SapModel" or "cSapModel" or "sap" or "m") parts = parts[1..];
        var (target, type) = Unwrap(model);
        var where = "cSapModel";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var property = Property(type, parts[i]) ?? throw new ToolInputException($"{where} has no '{parts[i]}'. Its parts: {string.Join(", ", Properties(type).Select(p => p.Name).Take(80))}.");
            object? value;
            try { value = EtabsApi.GetValue(property, target); }
            catch (TargetInvocationException ex) { throw new EtabsException($"The program's {where}.{property.Name} could not be read: {ex.InnerException?.Message ?? ex.Message}"); }
            if (value is null) throw new EtabsException($"The program's {where}.{property.Name} is empty in this version.");
            if (value is EtabsApi inner) (target, type) = (inner.Target, inner.ApiType);
            else (target, type) = (value, property.PropertyType.IsInterface ? property.PropertyType : value.GetType());
            where = property.Name;
        }
        var name = parts[^1];
        var methods = Methods(type).Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (methods.Count == 0)
        {
            if (Methods(type).Count == 0 && Properties(type).Count == 0) throw new ToolInputException($"{where} has nothing to call.");
            var close = Methods(type).Select(m => m.Name).Distinct().Where(n => n.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            throw new MissingMethodException($"{where} has no method {name} in this version." + (close.Count > 0 ? " Close: " + string.Join(", ", close) + "." : " Find it with search_api."));
        }
        // Overloads: the one whose parameter names hold most of the names given, then the newest (most parameters).
        var method = methods.OrderByDescending(m => named is null ? 0 : m.GetParameters().Count(p => Find(named, p.Name ?? "") is not null)).ThenByDescending(m => m.GetParameters().Length).First();
        return (target, type, method, where);
    }

    private static string? Find(IReadOnlyDictionary<string, object?> named, string name) =>
        named.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static PropertyInfo? Property(Type type, string name) =>
        Properties(type).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An interface's properties, its base interfaces' too (an interface's own GetProperties leaves those out).</summary>
    internal static IReadOnlyList<PropertyInfo> Properties(Type type) =>
        (type.IsInterface ? type.GetInterfaces().Prepend(type) : [type]).SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Where(p => p.GetIndexParameters().Length == 0).DistinctBy(p => p.Name).ToList();

    internal static IReadOnlyList<MethodInfo> Methods(Type type) =>
        (type.IsInterface ? type.GetInterfaces().Prepend(type) : [type]).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).ToList();

    private static object? Default(Type t)
    {
        if (t == typeof(string)) return "";
        if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    /// <summary>A value from JSON or from C# turned into the parameter's type: numbers, flags, text, enums by number or name, lists.</summary>
    internal static object? Convert(object? value, Type t, string name)
    {
        if (value is JsonElement je) value = FromJson(je);
        if (value is JsonNode jn) value = FromJson(JsonSerializer.SerializeToElement(jn));
        if (value is null) return Default(t);
        if (t.IsInstanceOfType(value) && !t.IsArray) return value;
        try
        {
            if (t.IsEnum)
            {
                if (value is string s)
                {
                    if (Enum.GetNames(t).FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) is { } found) return Enum.Parse(t, found);
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var si)) return Enum.ToObject(t, si);
                    throw new ToolInputException($"'{name}' is one of {string.Join(", ", Enum.GetNames(t))}.");
                }
                return Enum.ToObject(t, System.Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (t.IsArray)
            {
                var element = t.GetElementType()!;
                var items = value is System.Collections.IEnumerable list and not string ? list.Cast<object?>().ToList() : [value];
                var array = Array.CreateInstance(element, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(Convert(items[i], element, name), i);
                return array;
            }
            if (t == typeof(string)) return System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (t == typeof(bool) && value is string b) return b.Equals("true", StringComparison.OrdinalIgnoreCase) || b == "1";
            return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new ToolInputException($"'{name}' must be {Name(t)}: '{value}' is not.");
        }
    }

    private static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) && !e.GetRawText().Contains('.') && !e.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        _ => null,
    };

    /// <summary>An API value as JSON: numbers, text, flags, enums by name, lists of them.</summary>
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        Enum en => en.ToString(),
        double d => double.IsFinite(d) ? Math.Round(d, 9) : null,
        float f => double.IsFinite(f) ? Math.Round(f, 9) : null,
        int i => i,
        short or byte => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
        long l => l is >= int.MinValue and <= int.MaxValue ? (int)l : l,
        Array a => new JsonArray(a.Cast<object?>().Take(5000).Select(ToJson).ToArray()),
        _ => value.ToString(),
    };

    // ---- search and describe ----

    /// <summary>Every method reached from the model, by path, walking the API's interfaces (a few levels deep).</summary>
    public static IReadOnlyList<Member> Index(Type model)
    {
        var found = new List<Member>();
        var seen = new HashSet<Type>();
        void Walk(Type type, string path, int depth)
        {
            if (depth > 4 || !seen.Add(type)) return;
            foreach (var m in Methods(type)) found.Add(new(path.Length == 0 ? m.Name : $"{path}.{m.Name}", m));
            foreach (var p in Properties(type))
                if (p.PropertyType.IsInterface || p.PropertyType.IsClass && p.PropertyType != typeof(string) && p.PropertyType.Namespace == model.Namespace)
                    Walk(p.PropertyType, path.Length == 0 ? p.Name : $"{path}.{p.Name}", depth + 1);
            seen.Remove(type);
        }
        Walk(model, "", 0);
        return found;
    }

    /// <summary>The API's model type, from the live program (or a stand-in's own type).</summary>
    public static Type ModelType(ICsiLink e) =>
        (Type?)e.Raw("read the API's interfaces", m => Unwrap((object)m).Type) ?? throw new EtabsException("The program gave no API to read.");

    public static JsonObject Describe(MethodInfo m, string path)
    {
        var parameters = new JsonArray();
        foreach (var p in m.GetParameters())
        {
            var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            var o = new JsonObject { ["name"] = p.Name, ["type"] = Name(t), ["direction"] = p.ParameterType.IsByRef ? (p.IsOut ? "out" : "ref") : "in" };
            if (p.HasDefaultValue && p.DefaultValue is not DBNull) o["default"] = ToJson(p.DefaultValue);
            var e = t.IsArray ? t.GetElementType()! : t;
            if (e.IsEnum) o["values"] = new JsonObject(Enum.GetNames(e).Select(n => KeyValuePair.Create(n, (JsonNode?)System.Convert.ToInt32(Enum.Parse(e, n), CultureInfo.InvariantCulture))));
            parameters.Add(o);
        }
        return new JsonObject
        {
            ["path"] = path, ["returns"] = Name(m.ReturnType), ["parameters"] = parameters,
            ["reads_only"] = Reads(m.Name),
            ["call"] = Reads(m.Name) ? "call_api can run it." : "It changes the model: run it with the code tool (the engineer sees the code first).",
        };
    }

    private static string Describe(ParameterInfo p)
    {
        var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        return $"{(p.ParameterType.IsByRef ? "ref " : "")}{Name(t)} {p.Name}";
    }

    internal static string Name(Type t)
    {
        if (t.IsArray) return Name(t.GetElementType()!) + "[]";
        return t == typeof(int) ? "int" : t == typeof(double) ? "double" : t == typeof(string) ? "string" : t == typeof(bool) ? "bool" : t == typeof(void) ? "void" : t.Name;
    }
}
