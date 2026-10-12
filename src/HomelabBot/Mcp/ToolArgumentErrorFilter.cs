using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HomelabBot.Mcp;

// Without this the SDK answers "An error occurred invoking '<tool>'" and logs the caller's mistake as an error.
internal static class ToolArgumentErrorFilter
{
    // The SDK reports a missing required argument with this parameter name.
    private const string ArgumentsParameter = "arguments";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException { ParamName: ArgumentsParameter })
            {
                if (context.MatchedPrimitive is not McpServerTool { ProtocolTool: var tool })
                {
                    throw;
                }

                var arguments = context.Params?.Arguments;
                var problem = ex is ArgumentException
                    ? DescribeMissing(arguments, tool.InputSchema)
                    : DescribeWrongTypes(arguments, tool.InputSchema);

                // The same exception types can come from inside a tool. No fault found in the arguments: not the caller's mistake.
                if (problem is null)
                {
                    throw;
                }

                return Reject(context, tool, problem);
            }
        };

    // Both descriptions take names from the schema only: an argument name or value the caller made up never goes out.
    private static string? DescribeMissing(IDictionary<string, JsonElement>? arguments, JsonElement schema)
    {
        var missing = RequiredNames(schema)
            .Where(name => arguments is null || !arguments.ContainsKey(name))
            .Select(name => $"'{name}'")
            .ToList();
        return missing.Count == 0 ? null : $"required {string.Join(", ", missing)} is missing";
    }

    private static string? DescribeWrongTypes(IDictionary<string, JsonElement>? arguments, JsonElement schema)
    {
        if (arguments is null || !schema.TryGetProperty("properties", out var properties))
        {
            return null;
        }

        var wrong = arguments
            .Select(argument => properties.TryGetProperty(argument.Key, out var property)
                ? DescribeWrongType(argument.Key, argument.Value, property)
                : null)
            .OfType<string>()
            .ToList();
        return wrong.Count == 0 ? null : string.Join("; ", wrong);
    }

    private static string? DescribeWrongType(string name, JsonElement value, JsonElement property)
    {
        // No type in the schema: no claim.
        var allowed = AllowedTypes(property);
        return allowed.Count == 0 || allowed.Any(type => Accepts(type, value))
            ? null
            : $"'{name}' must be {string.Join(" or ", allowed)}, got {KindName(value)}";
    }

    private static IEnumerable<string> RequiredNames(JsonElement schema) =>
        schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(name => name.GetString() ?? string.Empty)
            : [];

    // "type" is one name or a list of names, e.g. ["string", "null"].
    private static List<string> AllowedTypes(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(allowed => allowed.GetString()).OfType<string>().ToList()
            : type.GetString() is { } single ? [single] : [];
    }

    private static bool Accepts(string schemaType, JsonElement value) => schemaType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => IsNumber(value, wholeOnly: true),
        "number" => IsNumber(value, wholeOnly: false),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,

        // A schema type not known here: no claim.
        _ => true,
    };

    // The SDK also reads a number from a string: "5" is a valid limit.
    private static bool IsNumber(JsonElement value, bool wholeOnly) => value.ValueKind switch
    {
        JsonValueKind.Number => !wholeOnly || value.TryGetInt64(out _),
        JsonValueKind.String => wholeOnly
            ? long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            : double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        _ => false,
    };

    private static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
        var kind => kind.ToString().ToLowerInvariant(),
    };

    // "query (string, required), limit (integer), since (string)".
    private static string DescribeShape(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) || !properties.EnumerateObject().Any())
        {
            return "none";
        }

        var required = RequiredNames(schema).ToHashSet(StringComparer.Ordinal);
        return string.Join(", ", properties.EnumerateObject().Select(property =>
        {
            var allowed = AllowedTypes(property.Value);
            var type = allowed.Count == 0 ? "any" : string.Join(" or ", allowed);
            return $"{property.Name} ({type}{(required.Contains(property.Name) ? ", required" : string.Empty)})";
        }));
    }

    private static CallToolResult Reject(RequestContext<CallToolRequestParams> context, Tool tool, string problem)
    {
        // The caller's mistake, not a fault of the bot: Warning, no stack trace, no caller content.
        context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolArgumentErrorFilter))
            .LogWarning("Rejected MCP call to {Tool}: {Problem}", tool.Name, problem);

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"Wrong arguments for '{tool.Name}': {problem}. Arguments: {DescribeShape(tool.InputSchema)}." }],
        };
    }
}
