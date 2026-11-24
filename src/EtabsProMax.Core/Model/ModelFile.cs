using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtabsProMax.Model;

/// <summary>ETABS ProMax's own model file (.epm): the model as JSON, which the Modeler saves, the Viewer and the
/// review pack read, and which converts to and from ETABS's .e2k without loss.</summary>
public static class ModelFile
{
    public const string Extension = ".epm";
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class Envelope
    {
        public string Format { get; set; } = "etabs-promax-model";
        public int Version { get; set; } = FormatVersion;
        public StructuralModel? Model { get; set; }
    }

    public static string ToJson(StructuralModel model) => JsonSerializer.Serialize(new Envelope { Model = model }, Options);

    public static StructuralModel FromJson(string json)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Options);
        if (envelope?.Model is null || envelope.Format != "etabs-promax-model")
            throw new InvalidDataException("This is not an ETABS ProMax model file.");
        if (envelope.Version > FormatVersion)
            throw new InvalidDataException($"This model was saved by a newer ETABS ProMax (file version {envelope.Version}). Update ETABS ProMax to open it.");
        return envelope.Model;
    }

    public static void Save(StructuralModel model, string path) => File.WriteAllText(path, ToJson(model));
    public static StructuralModel Load(string path) => FromJson(File.ReadAllText(path));
}
