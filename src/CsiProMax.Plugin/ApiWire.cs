using System.Text;

namespace CsiProMaxPlugin;

/// <summary>
/// How calls to a CSI program's API travel over a pipe between CSI ProMax's window and <see cref="ApiBridge"/>: one
/// message is a length and that many bytes; a value is a tag and what the tag says. The same file is compiled into
/// the plug-ins (.NET Standard 2.0, loaded by the program, or by Windows PowerShell for a window of its own) and into
/// CsiProMax.dll (.NET 8), so both ends always agree.
/// The values are what the API passes: numbers, text, yes/no, the program's enums (as numbers), lists of these, and
/// the API's own objects (cFrameObj and the like), which stay on the program's side and travel as a handle.
/// </summary>
internal static class ApiWire
{
    /// <summary>The pipe the plug-in serves in the program with this process id.</summary>
    public static string PipeName(int pid) => "CsiProMax.Api." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // The kinds of request.
    public const byte Hello = 1, Get = 2, Call = 3, Bye = 4;

    // The tags of a value.
    private const byte Null = 0, Bool = 1, Int = 2, Long = 3, Double = 4, String = 5, Array = 6, Handle = 7, Float = 8, Short = 9, Byte = 10, Enum = 11;

    /// <summary>The longest message either end accepts (a large table read as one array is some tens of MB).</summary>
    public const int Most = 512 * 1024 * 1024;

    public static void Send(Stream pipe, byte[] message)
    {
        var length = BitConverter.GetBytes(message.Length);
        pipe.Write(length, 0, 4);
        pipe.Write(message, 0, message.Length);
        pipe.Flush();
    }

    /// <summary>The next message, or null when the other end has gone.</summary>
    public static byte[]? Receive(Stream pipe)
    {
        var head = Fill(pipe, 4);
        if (head is null) return null;
        var length = BitConverter.ToInt32(head, 0);
        if (length < 0 || length > Most) throw new IOException("A message of " + length + " bytes came over the CSI ProMax pipe: the other end is not CSI ProMax.");
        return length == 0 ? new byte[0] : Fill(pipe, length) ?? throw new IOException("The CSI ProMax pipe closed in the middle of a message.");
    }

    private static byte[]? Fill(Stream pipe, int count)
    {
        var buffer = new byte[count];
        var at = 0;
        while (at < count)
        {
            var read = pipe.Read(buffer, at, count - at);
            if (read <= 0) return at == 0 ? null : throw new IOException("The CSI ProMax pipe closed in the middle of a message.");
            at += read;
        }
        return buffer;
    }

    public static BinaryWriter Writer(MemoryStream into) => new(into, Encoding.UTF8, true);

    public static BinaryReader Reader(byte[] message) => new(new MemoryStream(message), Encoding.UTF8, false);

    /// <summary>
    /// Writes one value. <paramref name="handleOf"/> gives the handle of an object that is not a plain value (the
    /// program's side registers it; CsiProMax's side gives back the handle it was sent).
    /// </summary>
    public static void Write(BinaryWriter w, object? value, Func<object, int> handleOf)
    {
        switch (value)
        {
            case null: w.Write(Null); return;
            case bool b: w.Write(Bool); w.Write(b); return;
            case int i: w.Write(Int); w.Write(i); return;
            case long l: w.Write(Long); w.Write(l); return;
            case double d: w.Write(Double); w.Write(d); return;
            case float f: w.Write(Float); w.Write(f); return;
            case short s: w.Write(Short); w.Write(s); return;
            case byte y: w.Write(Byte); w.Write(y); return;
            case string text: w.Write(String); w.Write(text); return;
            case System.Enum e: w.Write(Enum); w.Write(Convert.ToInt64(e, System.Globalization.CultureInfo.InvariantCulture)); return;
            case System.Array list:
                w.Write(Array);
                w.Write(list.Length);
                foreach (var item in list) Write(w, item, handleOf);
                return;
            case uint or ulong or ushort or sbyte or decimal or char:
                w.Write(Double); w.Write(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)); return;
            default: w.Write(Handle); w.Write(handleOf(value)); return;
        }
    }

    /// <summary>
    /// Reads one value as <paramref name="expected"/> (a parameter's type, by reference or not; null: as it came).
    /// <paramref name="fromHandle"/> turns a handle back into the object it stands for.
    /// </summary>
    public static object? Read(BinaryReader r, Type? expected, Func<int, Type?, object?> fromHandle)
    {
        if (expected is { IsByRef: true }) expected = expected.GetElementType();
        var tag = r.ReadByte();
        switch (tag)
        {
            case Null: return null;
            case Bool: return Fit(r.ReadBoolean(), expected);
            case Int: return Fit(r.ReadInt32(), expected);
            case Long: return Fit(r.ReadInt64(), expected);
            case Double: return Fit(r.ReadDouble(), expected);
            case Float: return Fit(r.ReadSingle(), expected);
            case Short: return Fit(r.ReadInt16(), expected);
            case Byte: return Fit(r.ReadByte(), expected);
            case String: return r.ReadString();
            case Enum:
                var number = r.ReadInt64();
                return Fit(expected is { IsEnum: true } ? number : (object)(int)number, expected);
            case Array:
                var count = r.ReadInt32();
                var element = expected is { IsArray: true } ? expected.GetElementType()! : typeof(object);
                var items = System.Array.CreateInstance(element, count);
                for (var i = 0; i < count; i++) items.SetValue(Read(r, element, fromHandle), i);
                return items;
            case Handle: return fromHandle(r.ReadInt32(), expected);
            default: throw new IOException("An unknown value (tag " + tag + ") came over the CSI ProMax pipe.");
        }
    }

    /// <summary>A plain value as the type it is wanted as: an enum from its number, a number of another kind.</summary>
    private static object Fit(object value, Type? expected)
    {
        if (expected is null || expected == typeof(object) || expected.IsInstanceOfType(value)) return value;
        if (expected.IsEnum) return System.Enum.ToObject(expected, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        var plain = Nullable.GetUnderlyingType(expected) ?? expected;
        if (plain.IsPrimitive || plain == typeof(decimal))
            try { return Convert.ChangeType(value, plain, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException) { return value; }
        return value;
    }

    /// <summary>A parameter type as both ends name it: the type's full name ("System.String[]&amp;" for a ref list of text).</summary>
    public static string Name(Type t) => t.FullName ?? t.Name;
}
