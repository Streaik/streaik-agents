using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Streaik.Agents;

internal sealed record AgentSkillBundle(string Id, string Name, string Description, string Instructions,
    IReadOnlyDictionary<string, byte[]> Resources)
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxFiles = 512;
    private const int MaxManifestBytes = 256 * 1024;

    internal static async Task<AgentSkillBundle> ReadAsync(Stream source, string? id, string? displayName, CancellationToken ct)
    {
        // Index and read archive entries in memory only. A bundled script is a resource, never executable code.
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxFiles * 2) throw new InvalidDataException("Too many ZIP entries.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = directory ? entry.FullName[..^1] : entry.FullName;
            if (!IsRelativePath(path) || !paths.Add(path) || (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new InvalidDataException("Invalid, duplicate, or linked ZIP entry.");
            if (directory) continue;
            if (files.Count == MaxFiles || entry.Length > MaxBytes - expanded)
                throw new InvalidDataException("Expanded skill ZIP exceeds the archive limits.");
            expanded += entry.Length;
            files.Add(path, entry);
        }

        var manifests = files.Values.Where(entry => entry.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests is not [{ Name: "SKILL.md" } manifest] || manifest.Length > MaxManifestBytes)
            throw new InvalidDataException("The ZIP must contain one SKILL.md within the manifest size limit.");
        var root = manifest.FullName[..^manifest.Name.Length];
        if (root.Count(c => c == '/') > 1 || files.Keys.Any(path => !path.StartsWith(root, StringComparison.Ordinal)))
            throw new InvalidDataException("The ZIP must contain a single skill folder.");

        var markdown = new UTF8Encoding(false, true).GetString(await ReadEntryAsync(manifest, ct)).TrimStart('\uFEFF');
        var (name, description, instructions) = ParseMarkdown(markdown);
        if (root.Length != 0 && root != name + "/")
            throw new InvalidDataException("The skill folder must match its name in SKILL.md.");

        var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in files)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (files.ContainsKey(path[..slash])) throw new InvalidDataException("A ZIP path cannot be both a file and a directory.");
            if (entry != manifest) resources.Add(path[root.Length..], await ReadEntryAsync(entry, ct));
        }

        return new(id ?? name, string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(), description, instructions, resources);
    }

    internal static bool IsRelativePath(string path) => path.Length is > 0 and <= 512
        && !path.Any(c => char.IsControl(c) || c is '\\' or ':')
        && path.Split('/').All(part => !string.IsNullOrWhiteSpace(part) && part is not ("." or ".."));

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        await stream.ReadExactlyAsync(bytes.AsMemory(), ct);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra.AsMemory(), ct) != 0)
            throw new InvalidDataException("ZIP entry exceeds its declared size.");
        return bytes;
    }

    private static (string Name, string Description, string Instructions) ParseMarkdown(string markdown)
    {
        using var reader = new StringReader(markdown);
        if (reader.ReadLine() != "---") throw new InvalidDataException("SKILL.md must start with YAML frontmatter.");
        var frontmatter = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null && line != "---") frontmatter.AppendLine(line);
        if (line is null) throw new InvalidDataException("Unclosed SKILL.md frontmatter.");

        // Read only top-level metadata. Do not deserialize YAML objects or resolve aliases.
        var parser = new Parser(new StringReader(frontmatter.ToString()));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        parser.Consume<MappingStart>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? name = null, description = null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            if (!keys.Add(key)) throw new InvalidDataException("Duplicate SKILL.md metadata key.");
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = parser.Consume<Scalar>().Value.Trim();
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = parser.Consume<Scalar>().Value.Trim();
            else parser.SkipThisAndNestedEvents();
        }
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();

        if (name is not { Length: > 0 and <= 64 } || !Regex.IsMatch(name, @"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")
            || string.IsNullOrWhiteSpace(description))
            throw new InvalidDataException("SKILL.md needs a valid name and a non-empty description.");
        return (name, description, reader.ReadToEnd().Trim());
    }
}
