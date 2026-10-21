using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexGUI;

internal sealed class CodexAutomationDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новая автоматизация";
    public string ProjectId { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "gpt-6-luna";
    public string Effort { get; set; } = "high";
    public string Cadence { get; set; } = "daily";
    public string TimeOfDay { get; set; } = "09:00";
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public bool UseWorktree { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset NextRunAtUtc { get; set; }
}

internal sealed class CodexAutomationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AutomationId { get; set; } = "";
    public string AutomationName { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string? WorktreePath { get; set; }
    public string? WorkerRunId { get; set; }
    public string? WorkerRuntimeDirectory { get; set; }
    public string? AppServerRuntimeDirectory { get; set; }
    public string? ThreadId { get; set; }
    public int WorkerProcessId { get; set; }
    public long WorkerStartTimeUtcTicks { get; set; }
    public int CoordinatorProcessId { get; set; }
    public long CoordinatorStartTimeUtcTicks { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = "preparing";
    public string Summary { get; set; } = "";
    public string? Error { get; set; }
    public bool Reviewed { get; set; }
}

internal static class CodexAutomationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "automations");
    public static string RunsDirectory => Path.Combine(Root, "runs");

    private static string DefinitionsPath => Path.Combine(Root, "automations.json");

    public static List<CodexAutomationDefinition> LoadDefinitions()
    {
        if (!File.Exists(DefinitionsPath)) return [];
        var definitions = JsonSerializer.Deserialize<List<CodexAutomationDefinition>>(File.ReadAllText(DefinitionsPath)) ?? [];
        foreach (var definition in definitions)
            if (definition.NextRunAtUtc == default)
                definition.NextRunAtUtc = CalculateNextRun(definition, DateTimeOffset.UtcNow);
        return definitions;
    }

    public static void SaveDefinitions(IReadOnlyList<CodexAutomationDefinition> definitions)
    {
        Directory.CreateDirectory(Root);
        WriteJsonAtomically(DefinitionsPath, definitions);
    }

    public static List<CodexAutomationRun> LoadRuns()
    {
        if (!Directory.Exists(RunsDirectory)) return [];
        var runs = new List<CodexAutomationRun>();
        foreach (var path in Directory.EnumerateFiles(RunsDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CodexAutomationRun>(File.ReadAllText(path)) is { } run)
                    runs.Add(run);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Не удалось прочитать результат автоматизации {0}: {1}", path, error.Message);
            }
        }
        return runs.OrderByDescending(run => run.StartedAtUtc).ToList();
    }

    public static void SaveRun(CodexAutomationRun run)
    {
        Directory.CreateDirectory(RunsDirectory);
        WriteJsonAtomically(Path.Combine(RunsDirectory, run.Id + ".json"), run);
    }

    public static void MarkRunReviewed(CodexAutomationRun run)
    {
        run.Reviewed = true;
        SaveRun(run);
    }

    public static DateTimeOffset CalculateNextRun(CodexAutomationDefinition definition, DateTimeOffset nowUtc)
    {
        if (!TimeOnly.TryParseExact(definition.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            throw new InvalidDataException("Время автоматизации должно быть в формате ЧЧ:ММ.");

        var localNow = nowUtc.ToLocalTime().DateTime;
        var date = localNow.Date;
        if (definition.Cadence == "weekly")
        {
            var days = ((int)definition.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7;
            date = date.AddDays(days);
        }
        else if (definition.Cadence != "daily")
            throw new InvalidDataException("Неизвестная периодичность автоматизации.");

        var candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Local);
        while (TimeZoneInfo.Local.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
        if (candidate <= localNow)
            candidate = candidate.AddDays(definition.Cadence == "weekly" ? 7 : 1);
        return new DateTimeOffset(candidate).ToUniversalTime();
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
