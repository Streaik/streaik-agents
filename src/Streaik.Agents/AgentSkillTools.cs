using System.ComponentModel;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Streaik.Agents;

internal sealed class AgentSkillTools(IReadOnlyList<AgentSkillBundle> skills)
{
    internal string Catalog() => JsonSerializer.Serialize(new
    {
        skills = skills.Select(skill => new { name = skill.Name, skill_id = skill.Id, description = skill.Description })
    });

    internal IList<AITool> Functions() =>
    [
        AIFunctionFactory.Create(Activate, "activate_skill"),
        AIFunctionFactory.Create(ReadResource, "read_skill_resource")
    ];

    [Description("Loads the body instructions for an enabled agent skill. Use this when one of the available skills matches the current task. After activation, use read_skill_resource to load referenced bundled files by relative path.")]
    private object Activate([Description("Exact enabled skill ID to activate, as listed in the system context.")] string skill_id)
    {
        if (Find(skill_id) is not { } skill) return Error("Pass an exact enabled skill_id from the system context.");
        var resourcePaths = skill.Resources.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var resources = resourcePaths.Length == 0 ? "<skill_resources />" : string.Join('\n',
            ["<skill_resources>", .. resourcePaths.Select(path => $"  <file>{Escape(path)}</file>"), "</skill_resources>"]);
        var text = $"<skill_content skill_id=\"{Escape(skill.Id)}\" name=\"{Escape(skill.Name)}\">\n{skill.Instructions}\n\n"
            + "Use read_skill_resource with this skill_id and a relative path from the resource list when you need bundled files referenced by the instructions.\n"
            + $"{resources}\n</skill_content>";
        return Success(new { skill = new { skill_id = skill.Id, name = skill.Name, description = skill.Description, resourcePaths, instructions = skill.Instructions } }, text);
    }

    [Description("Reads a bundled file from an enabled skill by relative path. Use this after activate_skill when the skill instructions reference scripts, references, or assets. Paths are relative to the skill root.")]
    private object ReadResource(
        [Description("Exact enabled skill ID that owns the resource. Reuse the skill_id passed to activate_skill.")] string skill_id,
        [Description("Relative path within the skill directory, for example references/REFERENCE.md or scripts/run.py.")] string path)
    {
        if (Find(skill_id) is not { } skill) return Error("Pass an exact enabled skill_id from the system context.");
        if (path is null || !AgentSkillBundle.IsRelativePath(path)) return Error("Pass a safe path relative to the skill folder.");
        if (!skill.Resources.TryGetValue(path, out var bytes)) return Error("The requested resource is not in this skill bundle.");

        var (mimeType, isText) = MimeType(path);
        if (isText)
        {
            var text = Encoding.UTF8.GetString(bytes);
            return Success(new { skillResource = new { skill_id = skill.Id, skillName = skill.Name, path, mimeType, text } },
                $"<skill_resource skill_id=\"{Escape(skill.Id)}\" name=\"{Escape(skill.Name)}\" path=\"{Escape(path)}\" mimeType=\"{mimeType}\">\n{text}\n</skill_resource>");
        }

        return Success(new { skillResource = new { skill_id = skill.Id, skillName = skill.Name, path, mimeType, encoding = "base64", data = Convert.ToBase64String(bytes) } },
            $"Binary skill resource {path} from skill {skill.Name}. mimeType={mimeType}. Base64 payload is available in structuredContent.skillResource.data.");
    }

    private AgentSkillBundle? Find(string id) => skills.FirstOrDefault(skill => skill.Id == id);

    private static string Escape(string text) => SecurityElement.Escape(text)!;

    private static object Success(object structuredContent, string text) => new { isError = false, structuredContent, content = new[] { new { type = "text", text } } };

    private static object Error(string text) => new { isError = true, content = new[] { new { type = "text", text } } };

    private static (string MimeType, bool Text) MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" => ("text/markdown", true),
        ".txt" or ".cs" or ".ps1" => ("text/plain", true),
        ".json" => ("application/json", true),
        ".yaml" or ".yml" => ("application/yaml", true),
        ".xml" => ("application/xml", true),
        ".html" or ".htm" => ("text/html", true),
        ".csv" => ("text/csv", true),
        ".tsv" => ("text/tab-separated-values", true),
        ".js" => ("text/javascript", true),
        ".ts" => ("text/typescript", true),
        ".jsx" => ("text/jsx", true),
        ".tsx" => ("text/tsx", true),
        ".css" => ("text/css", true),
        ".scss" => ("text/x-scss", true),
        ".py" => ("text/x-python", true),
        ".sh" => ("text/x-shellscript", true),
        ".sql" => ("application/sql", true),
        ".png" => ("image/png", false),
        ".jpg" or ".jpeg" => ("image/jpeg", false),
        ".gif" => ("image/gif", false),
        ".webp" => ("image/webp", false),
        ".svg" => ("image/svg+xml", false),
        ".pdf" => ("application/pdf", false),
        _ => ("application/octet-stream", Path.GetExtension(path).ToLowerInvariant() is ".toml" or ".ini" or ".cfg" or ".env" or ".gitignore" or ".dockerfile")
    };
}
