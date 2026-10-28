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

    private static string[] EnumerateDirectoriesSafely(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(path =>
                {
                    if (ExcludedDirectories.Contains(System.IO.Path.GetFileName(path))) return false;
                    try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
                })
                .Take(MaximumTopLevelDirectories)
                .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] EnumerateFilesSafely(string directory, string pattern, int limit)
    {
        try { return Directory.EnumerateFiles(directory, pattern).Take(limit).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static IReadOnlyList<WorkspaceMapNode> Read(string root, int mode)
    {
        // A workspace can be moved or a child folder can disappear during a refresh.
        // Keep the remaining map layers usable instead of failing the entire render.
        var directories = EnumerateDirectoriesSafely(root);
        if (mode == 0) return directories.Take(6).Select(path => new WorkspaceMapNode(System.IO.Path.GetFileName(path), path)).ToArray();
        var candidates = new[] { root }.Concat(directories).ToArray();
        if (mode == 2)
        {
            // File links can point outside the selected workspace. Never expose them as map targets.
            var files = candidates.SelectMany(directory => EnumerateFilesSafely(directory, "*", 500))
                .Where(path =>
                {
                    try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // A file may disappear or become inaccessible while the map refreshes.
                        return false;
                    }
                });
            return files.GroupBy(path => System.IO.Path.GetExtension(path).ToLowerInvariant())
                .OrderByDescending(group => group.Count()).Take(6)
                .Select(group => new WorkspaceMapNode($"{(group.Key.Length == 0 ? "без типа" : group.Key)} · {group.Count()}", group.First())).ToArray();
        }
        // Only six distinct labels can be displayed. Stop scanning once the map is full
        // instead of accumulating every dependency from large solution manifests.
        var nodes = new List<WorkspaceMapNode>(6);
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        bool AddNode(string label, string path)
        {
            if (seenLabels.Add(label))
                nodes.Add(new WorkspaceMapNode(label, path));
            return nodes.Count == 6;
        }

        foreach (var directory in candidates)
        {
            foreach (var manifest in EnumerateFilesSafely(directory, "*.csproj", 12))
            {
                try
                {
                    var manifestInfo = new FileInfo(manifest);
                    if ((manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0
                        || manifestInfo.Length > 1024 * 1024) continue;
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
                        if (!string.IsNullOrWhiteSpace(name) && AddNode(
                            reference.Name.LocalName == "ProjectReference" ? System.IO.Path.GetFileNameWithoutExtension(name) : name, manifest))
                            return nodes;
                    }
                    // A framework reference is a real dependency even when no NuGet package is used.
                    foreach (var framework in document.Descendants().Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
                        if (!string.IsNullOrWhiteSpace(framework.Value) && AddNode(framework.Value, manifest))
                            return nodes;
                }
                catch (System.Xml.XmlException) { if (AddNode("Ошибка XML", manifest)) return nodes; }
                // A manifest can be removed, locked or become inaccessible during refresh.
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var package = System.IO.Path.Combine(directory, "package.json");
            if (!File.Exists(package)) continue;
            try
            {
                var packageInfo = new FileInfo(package);
                if ((packageInfo.Attributes & FileAttributes.ReparsePoint) != 0
                    || packageInfo.Length > 1024 * 1024) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(package));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    if (AddNode("Ошибка JSON", package)) return nodes;
                    continue;
                }
                foreach (var key in new[] { "dependencies", "devDependencies" })
                    if (document.RootElement.TryGetProperty(key, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                        foreach (var dependency in dependencies.EnumerateObject())
                            if (AddNode(dependency.Name, package)) return nodes;
            }
            catch (JsonException) { if (AddNode("Ошибка JSON", package)) return nodes; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return nodes;
    }
}
