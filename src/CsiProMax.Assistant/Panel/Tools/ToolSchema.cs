using System.Text.Json;

namespace CsiProMax.Tools;

/// <summary>Single source for how a tool is described to Claude, used by both the API pane and the MCP bridge.</summary>
internal static class ToolSchema
{
    public static string Description(IEtabsTool tool) =>
        tool.Description + (tool.ModifiesModel ? " (Changes the model.)" : "");

    public static Dictionary<string, JsonElement> Properties(IEtabsTool tool) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.PropertiesJson) ?? [];

    public static object InputSchema(IEtabsTool tool) => new
    {
        type = "object",
        properties = Properties(tool),
        required = tool.Required,
    };
}
