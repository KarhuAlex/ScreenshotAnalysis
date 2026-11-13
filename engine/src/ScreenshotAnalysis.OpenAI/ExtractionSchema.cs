using System.Text.Json.Nodes;

namespace ScreenshotAnalysis.OpenAI;

internal static class ExtractionSchema
{
    public const string Version = AnalysisContract.SchemaVersion;
    public static JsonObject Create(ExtractionProfile profile)
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject NullableText() => new() { ["type"] = new JsonArray("string", "null") };
        static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };
        static JsonObject Obj(params (string Name, JsonObject Schema)[] fields)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, schema) in fields) { properties[name] = schema; required.Add(name); }
            return new() { ["type"] = "object", ["properties"] = properties,
                ["required"] = required, ["additionalProperties"] = false };
        }
        var label = Text();
        label["enum"] = new JsonArray(profile.Labels.Select(l => (JsonNode?)JsonValue.Create(l.Id)).ToArray());
        var role = Text();
        role["enum"] = new JsonArray("author", "mentioned", "gameUi", "unknown");
        return Obj(
            ("contexts", Array(Obj(("id", Text()), ("parentId", NullableText()), ("description", Text())))),
            ("classifications", Array(Obj(("label", label), ("contextId", Text()), ("evidence", Text())))),
            ("mentions", Array(Obj(("id", Text()), ("name", Text()), ("role", role),
                ("contextId", Text()), ("evidence", Text())))),
            ("warnings", Array(Text())));
    }
}
