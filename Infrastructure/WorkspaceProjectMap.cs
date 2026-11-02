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
    private const int MaximumNodeLabelCharacters = 120;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs"
    };

    // Manifests can contain very long names or control characters. Keep map labels
    // compact so an untrusted dependency name cannot overwhelm WPF layout.
    private static string CompactLabel(string value)
    {
        var result = new System.Text.StringBuilder(MaximumNodeLabelCharacters + 1);
        foreach (var character in value)
        {
            if (char.IsControl(character)) continue;
            if (result.Length >= MaximumNodeLabelCharacters)
            {
                if (char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
                result.Append('…');
                break;
            }
            result.Append(character);
        }
        return result.ToString().Trim();
    }

    private static string[] EnumerateDirectoriesSafely(string root)
    {
        // Keep the alphabetical selection even if directory enumeration fails midway.
        // A transient inaccessible entry should not erase folders already discovered.
        var comparer = Comparer<string>.Create((left, right) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(
                System.IO.Path.GetFileName(left), System.IO.Path.GetFileName(right));
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(left, right);
        });
        var selected = new SortedSet<string>(comparer);
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root))
            {
                if (ExcludedDirectories.Contains(System.IO.Path.GetFileName(path))) continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

                if (selected.Count < MaximumTopLevelDirectories)
                    selected.Add(path);
                else if (comparer.Compare(path, selected.Max!) < 0)
                {
                    selected.Remove(selected.Max!);
                    selected.Add(path);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the folders discovered before the filesystem interrupted the scan.
        }
        return selected.ToArray();
    }

    private static string[] EnumerateFilesSafely(string directory, string pattern, int limit)
    {
        var files = new List<string>(limit);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                files.Add(path);
                if (files.Count >= limit) break;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An entry can disappear during enumeration. Keep files already discovered.
        }
        return files.ToArray();
    }

    internal static IReadOnlyList<WorkspaceMapNode> Read(string root, int mode)
    {
        // A linked workspace root or parent can redirect the map outside the selected project.
        // Child directory and file links are filtered separately during enumeration.
        try
        {
            for (string? current = System.IO.Path.GetFullPath(root);
                 current is not null; current = System.IO.Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }

        // A workspace can be moved or a child folder can disappear during a refresh.
        // Keep the remaining map layers usable instead of failing the entire render.
        var directories = EnumerateDirectoriesSafely(root);
        if (mode == 0) return directories.Take(6).Select(path => new WorkspaceMapNode(CompactLabel(System.IO.Path.GetFileName(path)), path)).ToArray();
        var candidates = new[] { root }.Concat(directories).ToArray();
        if (mode == 2)
        {
            // Count file types without retaining every filename in grouping buffers.
            // File links may point outside the workspace, and linked worktrees have a .git metadata file.
            var counts = new Dictionary<string, (int Count, string FirstPath)>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in candidates)
            foreach (var path in EnumerateFilesSafely(directory, "*", 500))
            {
                if (string.Equals(System.IO.Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A file may disappear or become inaccessible during the refresh.
                    continue;
                }

                var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (counts.TryGetValue(extension, out var existing))
                    counts[extension] = (existing.Count + 1, existing.FirstPath);
                else
                    counts.Add(extension, (1, path));
            }

            return counts.OrderByDescending(item => item.Value.Count)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(item => new WorkspaceMapNode(
                    CompactLabel($"{(item.Key.Length == 0 ? "без типа" : item.Key)} · {item.Value.Count}"),
                    item.Value.FirstPath)).ToArray();
        }
        // Only six distinct labels can be displayed. Stop scanning once the map is full
        // instead of accumulating every dependency from large solution manifests.
        var nodes = new List<WorkspaceMapNode>(6);
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool AddNode(string label, string path)
        {
            label = CompactLabel(label);
            if (label.Length > 0 && seenLabels.Add(label))
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
                    // Share project files with editors so refreshing the map does not block saves.
                    using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                    // Project files are untrusted input: do not resolve DTDs or external entities.
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = 1024 * 1024
                    });
                    var document = XDocument.Load(reader);
                    foreach (var reference in document.Descendants().Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference"))
                    {
                        var name = (string?)reference.Attribute("Include");
                        // Central package management can declare an existing dependency with Update.
                        if (string.IsNullOrWhiteSpace(name) && reference.Name.LocalName == "PackageReference")
                            name = (string?)reference.Attribute("Update");
                        if (!string.IsNullOrWhiteSpace(name) && AddNode(
                            reference.Name.LocalName == "ProjectReference" ? System.IO.Path.GetFileNameWithoutExtension(name) : name, manifest))
                            return nodes;
                    }
                    // Multi-targeted projects list frameworks separated by semicolons.
                    // Display each target separately rather than as one unreadable label.
                    foreach (var framework in document.Descendants().Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
                        foreach (var target in framework.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                            if (AddNode(target, manifest))
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
                // A package manifest can grow after FileInfo.Length was checked.
                // Read at most one byte beyond the limit before parsing the snapshot.
                using var stream = new FileStream(package, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
                if (stream.Length > 1024 * 1024) continue;
                var bytes = new byte[1024 * 1024 + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var count = stream.Read(bytes, length, bytes.Length - length);
                    if (count == 0) break;
                    length += count;
                }
                if (length > 1024 * 1024) continue;
                // StreamReader retains the previous BOM handling (including UTF-16 manifests).
                using var snapshot = new MemoryStream(bytes, 0, length, writable: false);
                using var text = new StreamReader(snapshot);
                using var document = JsonDocument.Parse(text.ReadToEnd());
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
