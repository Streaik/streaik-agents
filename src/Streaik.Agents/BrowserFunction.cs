using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Streaik.Agents;

// A declaration only. The runtime must never invoke this function on behalf of the browser.
internal sealed class BrowserFunction(string name, string? description, JsonElement schema) : AIFunctionDeclaration
{
    public override string Name => name;
    public override string Description => description ?? string.Empty;
    public override JsonElement JsonSchema => schema;
}

internal sealed class BrowserFunctionCall(string id, string name, Dictionary<string, object?> arguments)
    : FunctionCallContent(id, name, arguments);
