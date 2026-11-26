using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;

namespace Streaik.Agents;

public sealed partial class AgentRuntime
{
    internal static IEnumerable<string> SkillProviders(AgentDefinition agent) => (agent.Skills ?? [])
        .Where(skill => SkillType(skill) == "skill_reference").Select(skill => SkillReference(skill).Provider);

    private async Task<IReadOnlyList<AgentSkillBundle>> LoadSkillsAsync(AgentDefinition agent, AgentModel answer,
        IReadOnlyDictionary<string, string> answerHeaders,
        Func<AgentDefinition, AgentModel, bool, ValueTask<IReadOnlyDictionary<string, string>?>> headersFor, CancellationToken ct)
    {
        if (agent.Skills is not { Count: > 0 } definitions) return [];
        var skills = new List<AgentSkillBundle>(definitions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var providerHeaders = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [GetProvider(answer)] = answerHeaders
        };

        foreach (var definition in definitions)
        {
            ct.ThrowIfCancellationRequested();
            AgentSkillBundle skill;
            switch (SkillType(definition))
            {
                case "inline":
                    skill = await ReadInlineSkillAsync(definition, ct);
                    break;
                case "skill_reference":
                    var (id, provider, path) = SkillReference(definition);
                    if (!providerHeaders.TryGetValue(provider, out var headers))
                        providerHeaders[provider] = headers = await headersFor(agent, new AgentModel { Id = $"{provider}/skill" }, false) ?? EmptyHeaders;
                    skill = await DownloadSkillAsync(id, path, ScopeHeaders(headers, new(StringComparer.Ordinal) { provider }), ct);
                    break;
                default:
                    throw InvalidSkill("Agent skill type must be 'inline' or 'skill_reference'.");
            }

            if (!ids.Add(skill.Id)) throw InvalidSkill("Agent skills must have distinct callable IDs.");
            skills.Add(skill);
        }
        return skills;
    }

    private static string SkillType(JsonElement skill)
    {
        if (skill.ValueKind != JsonValueKind.Object) throw InvalidSkill("An agent skill must be a JSON object.");
        var type = SkillProperty(skill, "type");
        return type.ValueKind == JsonValueKind.Undefined ? "inline" : SkillString(type);
    }

    private static JsonElement SkillProperty(JsonElement skill, string name)
    {
        foreach (var field in skill.EnumerateObject())
            if (field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return field.Value;
        return default;
    }

    private static string SkillString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()!
        : throw InvalidSkill("An agent skill has a missing or invalid string field.");

    private static async Task<AgentSkillBundle> ReadInlineSkillAsync(JsonElement skill, CancellationToken ct)
    {
        var name = SkillString(SkillProperty(skill, "name"));
        _ = SkillString(SkillProperty(skill, "description")); // The bundle's frontmatter is authoritative.
        var source = SkillProperty(skill, "source");
        if (source.ValueKind != JsonValueKind.Object
            || !SkillString(SkillProperty(source, "type")).Equals("base64", StringComparison.OrdinalIgnoreCase)
            || !SkillString(SkillProperty(source, "media_type")).Equals("application/zip", StringComparison.OrdinalIgnoreCase))
            throw InvalidSkill("An inline agent skill needs a base64 application/zip source.");
        var data = SkillString(SkillProperty(source, "data"));
        // Check decoded size before allocating; Convert also accepts whitespace in base64 values.
        if (data.Count(c => c is not (' ' or '\t' or '\r' or '\n')) > ((AgentSkillBundle.MaxBytes + 2) / 3) * 4)
            throw InvalidSkill("An inline skill ZIP exceeds 16 MiB.");
        try
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length is 0 or > AgentSkillBundle.MaxBytes) throw InvalidSkill("An inline skill ZIP must be between 1 byte and 16 MiB.");
            using var stream = new MemoryStream(bytes, writable: false);
            return await AgentSkillBundle.ReadAsync(stream, null, name, ct);
        }
        catch (Exception error) when (InvalidBundle(error) || error is FormatException)
        {
            throw InvalidSkill("An inline skill contains an invalid ZIP bundle or SKILL.md.");
        }
    }

    private static (string Id, string Provider, string Path) SkillReference(JsonElement skill)
    {
        var id = SkillString(SkillProperty(skill, "skill_id"));
        if (id.Split('/') is not [var provider, var skillId] || provider.Length == 0
            || provider.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) || !ValidSkillSegment(skillId))
            throw InvalidSkill("A referenced skill_id must be in 'provider/skill' form.");
        var path = $"v1/skills/{provider}/{Uri.EscapeDataString(skillId)}";
        var version = SkillProperty(skill, "version");
        if (version.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var value = SkillString(version);
            if (!ValidSkillSegment(value)) throw InvalidSkill("Invalid referenced skill version.");
            path += $"/versions/{Uri.EscapeDataString(value)}";
        }
        return (id, provider, path + "/content");
    }

    private static bool ValidSkillSegment(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && value is not ("." or "..") && !value.Any(c => char.IsControl(c) || c is '/' or '\\');

    private async Task<AgentSkillBundle> DownloadSkillAsync(string id, string path, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddHeaders(request, headers);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        // SendAsync's HttpClient timeout ends at the headers with ResponseHeadersRead; bound the body as well.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(gateway.Timeout);
        var downloadToken = timeout.Token;
        using var response = await gateway.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/zip", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > AgentSkillBundle.MaxBytes)
            throw new InvalidOperationException("The gateway did not return a skill ZIP within the size limit.");

        await using var body = await response.Content.ReadAsStreamAsync(downloadToken);
        using var zip = new MemoryStream();
        var buffer = new byte[81920];
        for (int read; (read = await body.ReadAsync(buffer.AsMemory(), downloadToken)) != 0;)
        {
            if (zip.Length + read > AgentSkillBundle.MaxBytes)
                throw new InvalidOperationException("The gateway skill ZIP exceeds 16 MiB.");
            zip.Write(buffer, 0, read);
        }
        zip.Position = 0;
        try { return await AgentSkillBundle.ReadAsync(zip, id, null, ct); }
        catch (Exception error) when (InvalidBundle(error))
        {
            throw new InvalidOperationException("The gateway returned an invalid skill ZIP or SKILL.md.", error);
        }
    }

    private static bool InvalidBundle(Exception error) => error is InvalidDataException or IOException or YamlException or DecoderFallbackException
        or NotSupportedException;

    private static AgentRequestException InvalidSkill(string message) => new(message, "invalid_agent_skill");
}
