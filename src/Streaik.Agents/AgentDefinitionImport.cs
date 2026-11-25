using System.Text.Json;

namespace Streaik.Agents;

// Catalog files may carry more AIHappey fields than the strict inline request schema.
internal static class AgentDefinitionImport
{
    public static AgentDefinition? Read(Stream json)
    {
        var source = JsonSerializer.Deserialize<ImportedAgent>(json, JsonSerializerOptions.Web);
        if (source is null || string.IsNullOrWhiteSpace(source.Name)) return null;

        var model = HasId(source.Model) ? source.Model : source.Models?.FirstOrDefault(HasId);
        return new AgentDefinition
        {
            Name = source.Name,
            Description = source.Description,
            Instructions = source.Instructions,
            Model = model is null ? null : new AgentModel { Id = model.Id },
            Transport = source.Transport ?? AgentDefinition.DefaultTransport
        };
    }

    private static bool HasId(ImportedModel? model) => !string.IsNullOrWhiteSpace(model?.Id);

    private sealed record ImportedAgent(string? Name, string? Description, string? Instructions,
        ImportedModel? Model, ImportedModel?[]? Models, string? Transport);

    private sealed record ImportedModel(string? Id);
}
