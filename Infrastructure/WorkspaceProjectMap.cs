using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodexGUI;

internal sealed record WorkspaceMapNode(string Label, string Path);

internal static class WorkspaceProjectMap
{
    // The map is an overview, not an exhaustive project scanner.
    private const int MaximumTopLevelDirectories = 128;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs"
    };

    internal static IReadOnlyList<WorkspaceMapNode> Read(string root, int mode)
    {
        var directories = Directory.EnumerateDirectories(root)
            .Where(path => !ExcludedDirectories.Contains(System.IO.Path.GetFileName(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Take(MaximumTopLevelDirectories)
            .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (mode == 0) return directories.Take(6).Select(path => new WorkspaceMapNode(System.IO.Path.GetFileName(path), path)).ToArray();
        var candidates = new[] { root }.Concat(directories).ToArray();
        if (mode == 2)
        {
            var files = candidates.SelectMany(directory => Directory.EnumerateFiles(directory).Take(500));
            return files.GroupBy(path => System.IO.Path.GetExtension(path).ToLowerInvariant())
                .OrderByDescending(group => group.Count()).Take(6)
                .Select(group => new WorkspaceMapNode($"{(group.Key.Length == 0 ? "без типа" : group.Key)} · {group.Count()}", group.First())).ToArray();
        }
        var nodes = new List<WorkspaceMapNode>();
        foreach (var directory in candidates)
        {
            foreach (var manifest in Directory.EnumerateFiles(directory, "*.csproj").Take(12))
            {
                var manifestInfo = new FileInfo(manifest);
                if ((manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0
                    || manifestInfo.Length > 1024 * 1024) continue;
                try
                {
                    // Project files are untrusted input: do not resolve DTDs or external entities.
                    using var reader = XmlReader.Create(manifest, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 1024 * 1024
                    });
                    var document = XDocument.Load(reader);
                    foreach (var reference in document.Descendants().Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference"))
                    {
                        var name = (string?)reference.Attribute("Include");
                        if (!string.IsNullOrWhiteSpace(name)) nodes.Add(new WorkspaceMapNode(
                            reference.Name.LocalName == "ProjectReference" ? System.IO.Path.GetFileNameWithoutExtension(name) : name, manifest));
                    }
                    // A framework reference is a real dependency even when no NuGet package is used.
                    foreach (var framework in document.Descendants().Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
                        if (!string.IsNullOrWhiteSpace(framework.Value)) nodes.Add(new WorkspaceMapNode(framework.Value, manifest));
                }
                catch (System.Xml.XmlException) { nodes.Add(new WorkspaceMapNode("Ошибка XML", manifest)); }
            }
            var package = System.IO.Path.Combine(directory, "package.json");
            if (!File.Exists(package)) continue;
            var packageInfo = new FileInfo(package);
            if ((packageInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || packageInfo.Length > 1024 * 1024) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(package));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    nodes.Add(new WorkspaceMapNode("Ошибка JSON", package));
                    continue;
                }
                foreach (var key in new[] { "dependencies", "devDependencies" })
                    if (document.RootElement.TryGetProperty(key, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                        foreach (var dependency in dependencies.EnumerateObject()) nodes.Add(new WorkspaceMapNode(dependency.Name, package));
            }
            catch (JsonException) { nodes.Add(new WorkspaceMapNode("Ошибка JSON", package)); }
        }
        return nodes.DistinctBy(node => node.Label).Take(6).ToArray();
    }
}
