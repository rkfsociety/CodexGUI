using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexGUI;

// A bounded, read-only inventory to help the local agent choose project-specific checks.
// Never open manifests, follow directory links or execute discovered tools.
internal static class LocalProjectInventory
{
    private static readonly string[] Markers =
    [
        "AGENTS.md", ".codex/rules.md", "README.md", "CONTRIBUTING.md",
        "global.json", "Directory.Build.props",
        "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock",
        "pyproject.toml", "requirements.txt", "uv.lock",
        "Cargo.toml", "Cargo.lock", "go.mod", "go.sum",
        "Makefile", "justfile", "CMakeLists.txt", "Dockerfile"
    ];

    internal static string FormatAgentHints(string workingDirectory, int maximumFiles = 16)
    {
        if (maximumFiles < 1) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (!Directory.Exists(workingDirectory)) return "";

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(workingDirectory);
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return "";

            void Include(string relativePath)
            {
                if (!seen.Add(relativePath)) return;
                var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0)
                    files.Add(relativePath);
            }

            foreach (var marker in Markers) Include(marker);
            foreach (var subdirectory in new[] { "", "Build", "Tests", "src" })
            {
                if (files.Count > maximumFiles) break;
                var folder = Path.Combine(root, subdirectory);
                if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;

                foreach (var pattern in new[] { "*.sln", "*.slnx", "*.csproj" })
                {
                    foreach (var path in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                    {
                        Include(Path.GetRelativePath(root, path).Replace('\\', '/'));
                        if (files.Count > maximumFiles) break;
                    }
                    if (files.Count > maximumFiles) break;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Files and folders may disappear during a refresh; omit the optional hints.
            return "";
        }

        if (files.Count == 0) return "";
        var shown = files.Take(maximumFiles).Select(GitRepositoryInspector.EscapeAgentField);
        var suffix = files.Count > maximumFiles ? " (список сокращён)" : "";
        return "\n\nЛокальные ориентиры проекта (только имена, без чтения содержимого): "
            + string.Join(", ", shown) + suffix
            + "\nПеред изменениями учитывайте проектные инструкции и выбирайте проверки по найденным файлам; команды не запускались.";
    }
}
