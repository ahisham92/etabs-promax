using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsiProMax.Model;

/// <summary>CSI ProMax's own model file: the model as JSON, which the Modeler saves, the Viewer and the review pack
/// read, and which converts to and from ETABS's .e2k without loss. Its extension names the program the model is for
/// (Ahmed 2026-11-27): .cpme for ETABS, .cpms for SAP2000 and .cpmf for SAFE, so Windows' pickers can show only the
/// files of the program the Engine works in. Files saved before that are .cpm and hold the program inside; they still
/// open, and the next save writes the new extension beside them.</summary>
public static class ModelFile
{
    /// <summary>The one extension of the files saved before each program had its own. Opened, never written.</summary>
    public const string Legacy = ".cpm";
    public const string Etabs = ".cpme", Sap2000 = ".cpms", Safe = ".cpmf";
    public const int FormatVersion = 1;

    /// <summary>Every extension a model file can have, the per-program ones first.</summary>
    public static readonly string[] Extensions = [Etabs, Sap2000, Safe, Legacy];

    /// <summary>The extension a model for this program is saved with ("" or anything unknown is ETABS, as the model's
    /// own default is).</summary>
    public static string ExtensionFor(string? target) => target switch { "sap2000" => Sap2000, "safe" => Safe, _ => Etabs };

    /// <summary>The program an extension names, or null for the old .cpm (its program is inside) and any other file.</summary>
    public static string? TargetOfExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        Etabs => "etabs", Sap2000 => "sap2000", Safe => "safe", _ => null,
    };

    /// <summary>Whether the file is a CSI ProMax model file, of any program or of the old kind.</summary>
    public static bool IsModelFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The model file a save beside <paramref name="programFile"/> writes for a model of this program.</summary>
    public static string PathFor(string programFile, string? target) => Path.ChangeExtension(programFile, ExtensionFor(target));

    /// <summary>
    /// The model file beside a program file (an .e2k, .s2k or .f2k the Modeler saved), of any of the four kinds, or
    /// null. When there are several (an old .cpm and the file the next save wrote, or a model saved for one program and
    /// then for another), the one written last is the model as it was last saved.
    /// </summary>
    public static string? Beside(string programFile) => Extensions
        .Select(x => Path.ChangeExtension(programFile, x))
        .Where(File.Exists)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = "csi-promax-model";
        public int Version { get; set; } = FormatVersion;
        public StructuralModel? Model { get; set; }
    }

    public static string ToJson(StructuralModel model) => JsonSerializer.Serialize(new Envelope { Model = model }, Options);

    public static StructuralModel FromJson(string json)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope?.Model is null || envelope.Format != "csi-promax-model")
            throw new InvalidDataException("This is not a CSI ProMax model file.");
        if (envelope.Version > FormatVersion)
            throw new InvalidDataException($"This model was saved by a newer CSI ProMax (file version {envelope.Version}). Update CSI ProMax to open it.");
        return envelope.Model;
    }

    public static void Save(StructuralModel model, string path) => File.WriteAllText(path, ToJson(model));
    /// <summary>Reads a model file. A per-program extension says the program the model is for, as Windows shows it;
    /// an old .cpm keeps the program it holds.</summary>
    public static StructuralModel Load(string path)
    {
        var model = FromJson(File.ReadAllText(path));
        if (TargetOfExtension(path) is { } target) model.Target = target;
        return model;
    }

    /// <summary>
    /// The program a file is for, without opening more of it than needed: by its extension (a model file of a
    /// program, or a program's own text file), and for an old .cpm by the program it holds. Null for any other file.
    /// </summary>
    public static string? ProgramOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        Etabs or ".e2k" or ".$et" => "etabs",
        Sap2000 or ".s2k" or ".$2k" => "sap2000",
        Safe or ".f2k" or ".$fk" => "safe",
        Legacy => FromJson(File.ReadAllText(path)).Target is { Length: > 0 } t ? t : "etabs",
        _ => null,
    };
}
