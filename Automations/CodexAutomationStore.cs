using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexGUI;

internal sealed record CodexAutomationProject(string Id, string Name, string Path, bool IsRegistered = true);

internal sealed class CodexAutomationDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новая автоматизация";
    public string ProjectId { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public List<string> ProjectIds { get; set; } = [];
    public Dictionary<string, string> ProjectPaths { get; set; } = new(StringComparer.Ordinal);
    public string? ExistingThreadId { get; set; }
    public string? ExistingThreadWorkingDirectory { get; set; }
    public string? ExistingThreadWorktreePath { get; set; }
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "gpt-6-luna";
    public string Effort { get; set; } = "high";
    public string Cadence { get; set; } = "daily";
    public string TimeOfDay { get; set; } = "09:00";
    public int IntervalMinutes { get; set; } = 60;
    public string? RecurrenceRule { get; set; }
    public DateTime? RecurrenceStartLocal { get; set; }
    public long RecurrenceOccurrencesBeforeNext { get; set; }
    public bool RecurrenceExhausted { get; set; }
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;
    public int DayOfMonth { get; set; } = 1;
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
    private static bool _definitionsSaveBlocked;
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexGUI", "automations");
    public static string RunsDirectory => Path.Combine(Root, "runs");

    private static string DefinitionsPath => Path.Combine(Root, "automations.json");

    public static List<CodexAutomationDefinition> LoadDefinitions()
    {
        if (!File.Exists(DefinitionsPath)) return [];
        try
        {
            var definitions = JsonSerializer.Deserialize<List<CodexAutomationDefinition>>(File.ReadAllText(DefinitionsPath))
                ?? throw new InvalidDataException("Файл расписаний пуст.");
            if (definitions.Any(definition => definition is null))
                throw new InvalidDataException("Файл расписаний содержит пустые записи.");
            foreach (var definition in definitions)
            {
                NormalizeDefinition(definition);
                if (definition.NextRunAtUtc == default && !definition.RecurrenceExhausted)
                    CalculateNextOccurrence(definition, DateTimeOffset.UtcNow);
            }
            return definitions;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            try
            {
                var backup = AtomicJsonFile.BackupUnreadable(DefinitionsPath);
                throw new InvalidDataException($"Расписания повреждены. Резервная копия: {backup}", error);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                _definitionsSaveBlocked = true;
                throw new InvalidDataException("Расписания повреждены, резервную копию создать не удалось. Сохранение заблокировано.", backupError);
            }
        }
    }

    internal static void NormalizeDefinition(CodexAutomationDefinition definition)
    {
        definition.ProjectIds ??= [];
        definition.ProjectPaths ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (definition.ProjectIds.Count == 0 && !string.IsNullOrWhiteSpace(definition.ProjectId))
            definition.ProjectIds.Add(definition.ProjectId);
        if (!string.IsNullOrWhiteSpace(definition.ProjectId) && !string.IsNullOrWhiteSpace(definition.ProjectPath))
            definition.ProjectPaths.TryAdd(definition.ProjectId, definition.ProjectPath);
    }

    internal static IReadOnlyList<CodexAutomationProject> ResolveAutomationProjects(
        CodexAutomationDefinition definition, IReadOnlyList<CodexAutomationProject> registeredProjects)
    {
        NormalizeDefinition(definition);
        var projects = new List<CodexAutomationProject>();
        foreach (var id in definition.ProjectIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var registered = registeredProjects.FirstOrDefault(project => project.Id == id);
            var path = registered?.Path ?? (definition.ProjectPaths.TryGetValue(id, out var savedPath)
                ? savedPath
                : id == definition.ProjectId ? definition.ProjectPath : "");
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidDataException($"Для проекта {id} в расписании не сохранён путь.");
            var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = registered?.Name ?? Path.GetFileName(trimmedPath);
            projects.Add(new CodexAutomationProject(id, name.Length > 0 ? name : path, path,
                IsRegistered: registered is not null));
        }
        return projects;
    }

    internal static void CalculateNextOccurrence(CodexAutomationDefinition definition, DateTimeOffset nowUtc,
        DateTimeOffset? previousOccurrenceUtc = null)
    {
        if (definition.Cadence == "rrule")
        {
            if (string.IsNullOrWhiteSpace(definition.RecurrenceRule) || definition.RecurrenceStartLocal is not { } startLocal)
                throw new InvalidDataException("Для расширенного расписания укажите DTSTART и RRULE.");
            var result = CodexRecurrenceRule.CalculateNext(definition.RecurrenceRule, startLocal, nowUtc,
                previousOccurrenceUtc, definition.RecurrenceOccurrencesBeforeNext);
            definition.RecurrenceOccurrencesBeforeNext = result.OccurrencesBeforeNext;
            definition.NextRunAtUtc = result.NextRunAtUtc ?? default;
            definition.RecurrenceExhausted = result.NextRunAtUtc is null;
            if (definition.RecurrenceExhausted) definition.Enabled = false;
            return;
        }

        definition.NextRunAtUtc = CalculateNextRun(definition, nowUtc, previousOccurrenceUtc);
        definition.RecurrenceOccurrencesBeforeNext = 0;
        definition.RecurrenceExhausted = false;
    }

    public static void SaveDefinitions(IReadOnlyList<CodexAutomationDefinition> definitions)
    {
        Directory.CreateDirectory(Root);
        if (_definitionsSaveBlocked)
            throw new InvalidDataException("Сохранение расписаний заблокировано до восстановления исходного файла.");
        AtomicJsonFile.Write(DefinitionsPath, definitions, JsonOptions);
    }

    public static List<CodexAutomationRun> LoadRuns()
    {
        if (!Directory.Exists(RunsDirectory)) return [];
        var runs = new List<CodexAutomationRun>();
        foreach (var path in Directory.EnumerateFiles(RunsDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<CodexAutomationRun>(File.ReadAllText(path)) is { } run
                    && Guid.TryParseExact(run.Id, "N", out _)
                    && string.Equals(Path.GetFileNameWithoutExtension(path), run.Id, StringComparison.OrdinalIgnoreCase))
                    runs.Add(run);
                else
                    System.Diagnostics.Trace.TraceWarning("Пропущен результат автоматизации с некорректным идентификатором: {0}", path);
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
        if (!Guid.TryParseExact(run.Id, "N", out _))
            throw new InvalidDataException("Недопустимый идентификатор запуска автоматизации.");
        AtomicJsonFile.Write(Path.Combine(RunsDirectory, run.Id + ".json"), run, JsonOptions);
    }

    public static void MarkRunReviewed(CodexAutomationRun run)
    {
        run.Reviewed = true;
        SaveRun(run);
    }

    public static DateTimeOffset CalculateNextRun(CodexAutomationDefinition definition, DateTimeOffset nowUtc,
        DateTimeOffset? previousOccurrenceUtc = null)
    {
        if (definition.Cadence == "interval")
        {
            if (definition.IntervalMinutes < 1)
                throw new InvalidDataException("Интервал должен быть положительным числом минут.");
            var period = TimeSpan.FromMinutes(definition.IntervalMinutes);
            var anchor = previousOccurrenceUtc is { } previous && previous <= nowUtc ? previous : nowUtc;
            var elapsedTicks = (nowUtc - anchor).Ticks;
            var intervalCount = elapsedTicks / period.Ticks + 1;
            return anchor.AddTicks(intervalCount * period.Ticks);
        }

        if (!TimeOnly.TryParseExact(definition.TimeOfDay, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            throw new InvalidDataException("Время автоматизации должно быть в формате ЧЧ:ММ.");

        var localNow = nowUtc.ToLocalTime().DateTime;
        DateTime candidate;
        if (definition.Cadence == "monthly")
        {
            if (definition.DayOfMonth is < 1 or > 31)
                throw new InvalidDataException("Число месяца должно быть от 1 до 31.");

            var month = new DateTime(localNow.Year, localNow.Month, 1);
            while (true)
            {
                if (definition.DayOfMonth <= DateTime.DaysInMonth(month.Year, month.Month))
                {
                    var date = month.AddDays(definition.DayOfMonth - 1);
                    candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Local);
                    if (candidate > localNow) break;
                }
                month = month.AddMonths(1);
            }
        }
        else
        {
            var date = localNow.Date;
            if (definition.Cadence == "weekly")
            {
                var days = ((int)definition.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7;
                date = date.AddDays(days);
            }
            else if (definition.Cadence != "daily")
                throw new InvalidDataException("Неизвестная периодичность автоматизации.");

            candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Local);
        }

        if (definition.Cadence != "monthly" && candidate <= localNow)
            candidate = candidate.AddDays(definition.Cadence == "weekly" ? 7 : 1);
        // A schedule can cross into a DST gap when advancing to tomorrow or next week.
        while (TimeZoneInfo.Local.IsInvalidTime(candidate)) candidate = candidate.AddMinutes(1);
        return new DateTimeOffset(candidate).ToUniversalTime();
    }

}
