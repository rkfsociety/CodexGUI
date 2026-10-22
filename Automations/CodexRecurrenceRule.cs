using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodexGUI;

internal sealed record CodexRecurrenceCalculation(DateTimeOffset? NextRunAtUtc, long OccurrencesBeforeNext);

internal static class CodexRecurrenceRule
{
    private const int MaximumPeriodsSearched = 1_000_000;
    private const int MaximumCandidatesPerPeriod = 500_000;

    private enum Frequency { Secondly, Minutely, Hourly, Daily, Weekly, Monthly, Yearly }
    private sealed record DaySpecifier(int? Ordinal, DayOfWeek Day);
    private sealed record UntilBound(DateTime? Local, DateTimeOffset? Utc);
    private sealed record ParsedRule(Frequency Frequency, int Interval, long? Count, UntilBound? Until,
        int[] BySecond, int[] ByMinute, int[] ByHour, DaySpecifier[] ByDay, int[] ByMonthDay,
        int[] ByYearDay, int[] ByWeekNo, int[] ByMonth, int[] BySetPos, DayOfWeek WeekStart);

    public static void Validate(string ruleText) => _ = Parse(ruleText);

    public static CodexRecurrenceCalculation CalculateNext(string ruleText, DateTime startAtLocal,
        DateTimeOffset nowUtc, DateTimeOffset? previousOccurrenceUtc, long occurrencesBeforeNext)
    {
        var rule = Parse(ruleText);
        startAtLocal = DateTime.SpecifyKind(startAtLocal, DateTimeKind.Unspecified);
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local).DateTime;
        var searchLocal = previousOccurrenceUtc?.ToLocalTime().DateTime ?? nowLocal;
        if (rule.Until is { Utc: { } untilUtc } && nowUtc > untilUtc
            || rule.Until is { Local: { } untilLocal } && nowLocal > untilLocal)
            return new CodexRecurrenceCalculation(null, occurrencesBeforeNext);

        var count = previousOccurrenceUtc is null ? 0 : occurrencesBeforeNext;
        if (rule.Count is { } countLimit && count >= countLimit)
            return new CodexRecurrenceCalculation(null, count);

        var periodIndex = previousOccurrenceUtc is null && rule.Count is not null
            ? 0
            : GetPeriodIndex(startAtLocal, searchLocal, rule);

        for (var scanned = 0; scanned < MaximumPeriodsSearched; scanned++, periodIndex++)
        {
            var periodStart = GetPeriodStart(startAtLocal, periodIndex, rule);
            if (periodStart is null) return new CodexRecurrenceCalculation(null, count);
            if (periodIndex > 0 && (int)rule.Frequency <= (int)Frequency.Daily && HasDateFilters(rule)
                && !MatchesDate(periodStart.Value.Date, periodStart.Value.Year, startAtLocal, rule))
            {
                var nextMatchingDate = FindNextMatchingDate(periodStart.Value.Date.AddDays(1), startAtLocal, rule);
                if (nextMatchingDate is null) return new CodexRecurrenceCalculation(null, count);
                var nextIndex = GetPeriodIndex(startAtLocal, nextMatchingDate.Value, rule);
                DateTime? alignedStart;
                while ((alignedStart = GetPeriodStart(startAtLocal, nextIndex, rule)) is not null
                    && alignedStart.Value.Date < nextMatchingDate.Value.Date)
                    nextIndex++;
                if (alignedStart is null) return new CodexRecurrenceCalculation(null, count);
                periodIndex = nextIndex - 1;
                continue;
            }
            List<DateTime> candidates;
            try { candidates = GetPeriodOccurrences(startAtLocal, periodStart.Value, rule); }
            catch (ArgumentOutOfRangeException) { return new CodexRecurrenceCalculation(null, count); }

            // DTSTART belongs to the recurrence set even when the RRULE's BY parts do not match it.
            if (periodIndex == 0 && !candidates.Contains(startAtLocal))
            {
                candidates.Add(startAtLocal);
                candidates.Sort();
            }

            foreach (var candidateLocal in candidates)
            {
                if (candidateLocal < startAtLocal || !TryToUtc(candidateLocal, out var candidateUtc)) continue;
                if (previousOccurrenceUtc is { } previous && candidateUtc < previous) continue;
                if (IsAfterUntil(candidateLocal, candidateUtc, rule.Until))
                    return new CodexRecurrenceCalculation(null, count);

                if (candidateUtc <= nowUtc)
                {
                    if (rule.Count is { } maxCount)
                    {
                        count++;
                        if (count >= maxCount) return new CodexRecurrenceCalculation(null, count);
                    }
                    continue;
                }

                return new CodexRecurrenceCalculation(candidateUtc, rule.Count is null ? 0 : count);
            }
        }

        throw new InvalidDataException("RRULE не дал следующего запуска в допустимом диапазоне поиска.");
    }

    private static bool HasDateFilters(ParsedRule rule) => rule.ByMonth.Length > 0 || rule.ByMonthDay.Length > 0
        || rule.ByYearDay.Length > 0 || rule.ByWeekNo.Length > 0 || rule.ByDay.Length > 0;

    private static DateTime? FindNextMatchingDate(DateTime firstDate, DateTime startAtLocal, ParsedRule rule)
    {
        var date = firstDate.Date;
        // Gregorian date and weekday patterns repeat every 400 years.
        for (var scanned = 0; scanned < 146_097; scanned++)
        {
            if (MatchesDate(date, date.Year, startAtLocal, rule)) return date;
            try { date = date.AddDays(1); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }

    private static ParsedRule Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
            throw new InvalidDataException("Укажите правило RRULE длиной от 1 до 4096 символов.");
        text = text.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) text = text[6..];

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0 || equals == part.Length - 1)
                throw new InvalidDataException($"Некорректная часть RRULE: {part}");
            var key = part[..equals].Trim();
            if (!parts.TryAdd(key, part[(equals + 1)..].Trim()))
                throw new InvalidDataException($"Часть {key} указана в RRULE больше одного раза.");
        }

        foreach (var key in parts.Keys)
            if (!IsSupportedPart(key)) throw new InvalidDataException($"Часть RRULE {key} пока не поддерживается.");
        if (!parts.TryGetValue("FREQ", out var frequencyText)
            || !Enum.TryParse<Frequency>(frequencyText, ignoreCase: true, out var frequency))
            throw new InvalidDataException("RRULE должен содержать FREQ со значением SECONDLY, MINUTELY, HOURLY, DAILY, WEEKLY, MONTHLY или YEARLY.");

        var interval = ParseSingleInt(parts, "INTERVAL", defaultValue: 1, minimum: 1, maximum: int.MaxValue);
        var count = parts.TryGetValue("COUNT", out var countText)
            ? ParseLong(countText, "COUNT", minimum: 1, maximum: int.MaxValue)
            : (long?)null;
        if (count is not null && parts.ContainsKey("UNTIL"))
            throw new InvalidDataException("В одном RRULE нельзя одновременно указывать COUNT и UNTIL.");

        var bySecond = ParseIntList(parts, "BYSECOND", 0, 59);
        var byMinute = ParseIntList(parts, "BYMINUTE", 0, 59);
        var byHour = ParseIntList(parts, "BYHOUR", 0, 23);
        var byDay = ParseByDay(parts);
        var byMonthDay = ParseIntList(parts, "BYMONTHDAY", -31, 31, forbidZero: true);
        var byYearDay = ParseIntList(parts, "BYYEARDAY", -366, 366, forbidZero: true);
        var byWeekNo = ParseIntList(parts, "BYWEEKNO", -53, 53, forbidZero: true);
        var byMonth = ParseIntList(parts, "BYMONTH", 1, 12);
        var bySetPos = ParseIntList(parts, "BYSETPOS", -366, 366, forbidZero: true);
        var until = parts.TryGetValue("UNTIL", out var untilText) ? ParseUntil(untilText) : null;
        var weekStart = parts.TryGetValue("WKST", out var weekStartText)
            ? ParseWeekday(weekStartText, "WKST")
            : DayOfWeek.Monday;

        ValidateCombinations(frequency, byDay, byMonthDay, byYearDay, byWeekNo,
            byMonth, bySetPos, bySecond, byMinute, byHour);
        return new ParsedRule(frequency, interval, count, until, bySecond, byMinute, byHour,
            byDay, byMonthDay, byYearDay, byWeekNo, byMonth, bySetPos, weekStart);
    }

    private static bool IsSupportedPart(string key) => key.Equals("FREQ", StringComparison.OrdinalIgnoreCase)
        || key.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase)
        || key.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
        || key.Equals("UNTIL", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYSECOND", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYMINUTE", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYHOUR", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYDAY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYMONTHDAY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYYEARDAY", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYWEEKNO", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYMONTH", StringComparison.OrdinalIgnoreCase)
        || key.Equals("BYSETPOS", StringComparison.OrdinalIgnoreCase)
        || key.Equals("WKST", StringComparison.OrdinalIgnoreCase);

    private static int ParseSingleInt(IReadOnlyDictionary<string, string> parts, string key,
        int defaultValue, int minimum, int maximum)
    {
        if (!parts.TryGetValue(key, out var value)) return defaultValue;
        return (int)ParseLong(value, key, minimum, maximum);
    }

    private static long ParseLong(string value, string name, long minimum, long maximum)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > maximum)
            throw new InvalidDataException($"Значение {name} должно быть числом от {minimum} до {maximum}.");
        return parsed;
    }

    private static int[] ParseIntList(IReadOnlyDictionary<string, string> parts, string key,
        int minimum, int maximum, bool forbidZero = false)
    {
        if (!parts.TryGetValue(key, out var value)) return [];
        var tokens = value.Split(',');
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"Часть {key} содержит пустое значение.");
        var numbers = new List<int>();
        foreach (var token in tokens)
        {
            var parsed = ParseLong(token.Trim(), key, minimum, maximum);
            if (forbidZero && parsed == 0) throw new InvalidDataException($"Значение 0 недопустимо в {key}.");
            numbers.Add((int)parsed);
        }
        return numbers.Distinct().Order().ToArray();
    }

    private static DaySpecifier[] ParseByDay(IReadOnlyDictionary<string, string> parts)
    {
        if (!parts.TryGetValue("BYDAY", out var value)) return [];
        var tokens = value.Split(',');
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("BYDAY содержит пустое значение.");
        var days = new List<DaySpecifier>();
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().ToUpperInvariant();
            if (token.Length < 2) throw new InvalidDataException($"Некорректное значение BYDAY: {token}");
            var weekday = ParseWeekday(token[^2..], "BYDAY");
            var ordinalText = token[..^2];
            int? ordinal = null;
            if (ordinalText.Length > 0)
            {
                var parsed = ParseLong(ordinalText, "BYDAY", -53, 53);
                if (parsed == 0) throw new InvalidDataException("Порядковый номер BYDAY не может быть равен 0.");
                ordinal = (int)parsed;
            }
            days.Add(new DaySpecifier(ordinal, weekday));
        }
        return days.Distinct().ToArray();
    }

    private static DayOfWeek ParseWeekday(string value, string name) => value.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => throw new InvalidDataException($"Некорректный день недели в {name}: {value}")
    };

    private static UntilBound ParseUntil(string value)
    {
        const string format = "yyyyMMdd'T'HHmmss";
        if (value.EndsWith('Z'))
        {
            if (!DateTime.TryParseExact(value[..^1], format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                throw new InvalidDataException("UNTIL должен иметь формат yyyyMMdd'T'HHmmssZ.");
            return new UntilBound(null, new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
        }
        if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new InvalidDataException("UNTIL должен иметь формат yyyyMMdd'T'HHmmssZ или yyyyMMdd'T'HHmmss.");
        return new UntilBound(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), null);
    }

    private static void ValidateCombinations(Frequency frequency, DaySpecifier[] byDay,
        int[] byMonthDay, int[] byYearDay, int[] byWeekNo, int[] byMonth, int[] bySetPos,
        int[] bySecond, int[] byMinute, int[] byHour)
    {
        if (frequency == Frequency.Weekly && byMonthDay.Length > 0)
            throw new InvalidDataException("BYMONTHDAY нельзя использовать с FREQ=WEEKLY.");
        if ((frequency is Frequency.Daily or Frequency.Weekly or Frequency.Monthly) && byYearDay.Length > 0)
            throw new InvalidDataException("BYYEARDAY нельзя использовать с FREQ=DAILY, WEEKLY или MONTHLY.");
        if (frequency != Frequency.Yearly && byWeekNo.Length > 0)
            throw new InvalidDataException("BYWEEKNO можно использовать только с FREQ=YEARLY.");
        if (byDay.Any(day => day.Ordinal is not null) && frequency is not (Frequency.Monthly or Frequency.Yearly))
            throw new InvalidDataException("Порядковый BYDAY можно использовать только с FREQ=MONTHLY или YEARLY.");
        if (frequency == Frequency.Yearly && byWeekNo.Length > 0 && byDay.Any(day => day.Ordinal is not null))
            throw new InvalidDataException("Порядковый BYDAY нельзя сочетать с FREQ=YEARLY и BYWEEKNO.");
        if (bySetPos.Length > 0 && bySecond.Length + byMinute.Length + byHour.Length + byDay.Length
                + byMonthDay.Length + byYearDay.Length + byWeekNo.Length + byMonth.Length == 0)
            throw new InvalidDataException("BYSETPOS нужно сочетать хотя бы с одной частью BYxxx.");
        var maximumSetSize = frequency switch
        {
            Frequency.Secondly => 1,
            Frequency.Minutely => 60,
            Frequency.Hourly => 3_600,
            Frequency.Daily => 86_400,
            _ => MaximumCandidatesPerPeriod
        };
        if (bySetPos.Any(position => Math.Abs(position) > maximumSetSize))
            throw new InvalidDataException("BYSETPOS ссылается на позицию, которую период RRULE не может содержать.");
    }

    private static long GetPeriodIndex(DateTime start, DateTime reference, ParsedRule rule)
    {
        if (reference <= start) return 0;
        var interval = rule.Interval;
        return rule.Frequency switch
        {
            Frequency.Secondly => Math.Max(0, (reference - start).Ticks / TimeSpan.TicksPerSecond / interval),
            Frequency.Minutely => Math.Max(0, (reference - start).Ticks / TimeSpan.TicksPerMinute / interval),
            Frequency.Hourly => Math.Max(0, (reference - start).Ticks / TimeSpan.TicksPerHour / interval),
            Frequency.Daily => Math.Max(0, (reference.Date - start.Date).Days / interval),
            Frequency.Weekly => Math.Max(0, (StartOfWeek(reference.Date, rule.WeekStart) - StartOfWeek(start.Date, rule.WeekStart)).Days / (7L * interval)),
            Frequency.Monthly => Math.Max(0, ((reference.Year - start.Year) * 12L + reference.Month - start.Month) / interval),
            Frequency.Yearly => Math.Max(0, (reference.Year - start.Year) / interval),
            _ => 0
        };
    }

    private static DateTime? GetPeriodStart(DateTime start, long periodIndex, ParsedRule rule)
    {
        try
        {
            var units = checked(periodIndex * rule.Interval);
            return rule.Frequency switch
            {
                Frequency.Secondly => start.AddTicks(checked(units * TimeSpan.TicksPerSecond)),
                Frequency.Minutely => start.AddTicks(checked(units * TimeSpan.TicksPerMinute)),
                Frequency.Hourly => start.AddTicks(checked(units * TimeSpan.TicksPerHour)),
                Frequency.Daily => DateTime.SpecifyKind(start.Date.AddDays(units), DateTimeKind.Unspecified),
                Frequency.Weekly => DateTime.SpecifyKind(StartOfWeek(start.Date, rule.WeekStart).AddDays(checked(units * 7)), DateTimeKind.Unspecified),
                Frequency.Monthly when units <= int.MaxValue => DateTime.SpecifyKind(new DateTime(start.Year, start.Month, 1).AddMonths((int)units), DateTimeKind.Unspecified),
                Frequency.Yearly when units <= int.MaxValue => DateTime.SpecifyKind(new DateTime(start.Year, 1, 1).AddYears((int)units), DateTimeKind.Unspecified),
                _ => null
            };
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static List<DateTime> GetPeriodOccurrences(DateTime start, DateTime periodStart, ParsedRule rule)
    {
        var dates = GetPeriodDates(start, periodStart, rule);
        var hourValues = GetTimeComponents(rule.ByHour, periodStart.Hour, start.Hour, (int)rule.Frequency > 2);
        var minuteValues = GetTimeComponents(rule.ByMinute, periodStart.Minute, start.Minute, (int)rule.Frequency > 1);
        var secondValues = GetTimeComponents(rule.BySecond, periodStart.Second, start.Second, (int)rule.Frequency > 0);
        var occurrences = new List<DateTime>();
        foreach (var date in dates)
        {
            if (!MatchesDate(date, periodStart.Year, start, rule)) continue;
            foreach (var hour in hourValues)
            foreach (var minute in minuteValues)
            foreach (var second in secondValues)
            {
                if (occurrences.Count >= MaximumCandidatesPerPeriod)
                    throw new InvalidDataException("RRULE расширяет один период до слишком большого числа запусков.");
                occurrences.Add(new DateTime(date.Year, date.Month, date.Day, hour, minute, second, DateTimeKind.Unspecified));
            }
        }
        occurrences = occurrences.Distinct().Order().ToList();
        if (rule.BySetPos.Length == 0 || occurrences.Count == 0) return occurrences;

        var selectedPositions = new HashSet<int>();
        foreach (var position in rule.BySetPos)
        {
            var index = position > 0 ? position - 1 : occurrences.Count + position;
            if (index >= 0 && index < occurrences.Count) selectedPositions.Add(index);
        }
        return selectedPositions.Order().Select(index => occurrences[index]).ToList();
    }

    private static int[] GetTimeComponents(int[] values, int periodValue, int startValue, bool expands)
    {
        if (expands) return values.Length > 0 ? values : [startValue];
        if (values.Length > 0 && !values.Contains(periodValue)) return [];
        return [periodValue];
    }

    private static List<DateTime> GetPeriodDates(DateTime start, DateTime periodStart, ParsedRule rule)
    {
        var dates = new List<DateTime>();
        switch (rule.Frequency)
        {
            case Frequency.Secondly:
            case Frequency.Minutely:
            case Frequency.Hourly:
            case Frequency.Daily:
                dates.Add(periodStart.Date);
                break;
            case Frequency.Weekly:
                var weekDays = rule.ByDay.Length > 0
                    ? rule.ByDay.Select(day => day.Day).Distinct().ToArray()
                    : [start.DayOfWeek];
                foreach (var weekday in weekDays)
                    dates.Add(StartOfWeek(periodStart.Date, rule.WeekStart).AddDays((int)((weekday - rule.WeekStart + 7) % 7)));
                break;
            case Frequency.Monthly:
                var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);
                if (rule.ByMonthDay.Length > 0)
                {
                    foreach (var monthDay in rule.ByMonthDay)
                    {
                        var day = monthDay > 0 ? monthDay : daysInMonth + monthDay + 1;
                        if (day >= 1 && day <= daysInMonth) dates.Add(new DateTime(periodStart.Year, periodStart.Month, day));
                    }
                }
                else if (rule.ByDay.Length > 0)
                {
                    for (var day = 1; day <= daysInMonth; day++) dates.Add(new DateTime(periodStart.Year, periodStart.Month, day));
                }
                else if (start.Day <= daysInMonth)
                    dates.Add(new DateTime(periodStart.Year, periodStart.Month, start.Day));
                break;
            case Frequency.Yearly:
                if (rule.ByWeekNo.Length > 0)
                {
                    var first = WeekYearStart(periodStart.Year, rule.WeekStart);
                    var next = WeekYearStart(periodStart.Year + 1, rule.WeekStart);
                    for (var date = first; date < next; date = date.AddDays(1)) dates.Add(date);
                }
                else if (rule.ByYearDay.Length > 0 || rule.ByMonthDay.Length > 0 || rule.ByDay.Length > 0)
                {
                    var days = DateTime.IsLeapYear(periodStart.Year) ? 366 : 365;
                    var first = new DateTime(periodStart.Year, 1, 1);
                    for (var day = 0; day < days; day++) dates.Add(first.AddDays(day));
                }
                else if (rule.ByMonth.Length > 0)
                {
                    foreach (var month in rule.ByMonth)
                        if (start.Day <= DateTime.DaysInMonth(periodStart.Year, month))
                            dates.Add(new DateTime(periodStart.Year, month, start.Day));
                }
                else if (start.Day <= DateTime.DaysInMonth(periodStart.Year, start.Month))
                    dates.Add(new DateTime(periodStart.Year, start.Month, start.Day));
                break;
        }
        return dates.Order().ToList();
    }

    private static bool MatchesDate(DateTime date, int periodYear, DateTime start, ParsedRule rule)
    {
        if (rule.ByMonth.Length > 0 && !rule.ByMonth.Contains(date.Month)) return false;
        if (rule.ByMonthDay.Length > 0)
        {
            var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
            if (!rule.ByMonthDay.Any(value => (value > 0 ? value : daysInMonth + value + 1) == date.Day)) return false;
        }
        if (rule.ByYearDay.Length > 0)
        {
            if (date.Year != periodYear) return false;
            var daysInYear = DateTime.IsLeapYear(periodYear) ? 366 : 365;
            if (!rule.ByYearDay.Any(value => (value > 0 ? value : daysInYear + value + 1) == date.DayOfYear)) return false;
        }
        if (rule.ByWeekNo.Length > 0)
        {
            var (weekYear, weekNumber) = GetWeekNumber(date, rule.WeekStart);
            var weeksInYear = (int)((WeekYearStart(weekYear + 1, rule.WeekStart) - WeekYearStart(weekYear, rule.WeekStart)).TotalDays / 7);
            if (weekYear != periodYear || !rule.ByWeekNo.Any(value => (value > 0 ? value : weeksInYear + value + 1) == weekNumber)) return false;
        }
        if (rule.ByDay.Length > 0)
        {
            if (!rule.ByDay.Any(day => MatchesDaySpecifier(date, day, periodYear, rule))) return false;
        }
        else if (rule.Frequency == Frequency.Weekly && date.DayOfWeek != start.DayOfWeek)
            return false;
        else if (rule.Frequency == Frequency.Yearly && rule.ByWeekNo.Length > 0 && date.DayOfWeek != start.DayOfWeek)
            return false;
        return true;
    }

    private static bool MatchesDaySpecifier(DateTime date, DaySpecifier specifier, int periodYear, ParsedRule rule)
    {
        if (date.DayOfWeek != specifier.Day) return false;
        if (specifier.Ordinal is not { } ordinal) return true;
        var withinMonth = rule.Frequency == Frequency.Monthly
            || rule.Frequency == Frequency.Yearly && rule.ByMonth.Length > 0;
        var first = withinMonth ? new DateTime(date.Year, date.Month, 1) : new DateTime(periodYear, 1, 1);
        var last = withinMonth ? new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month))
            : new DateTime(periodYear, 12, 31);
        var firstOffset = ((int)specifier.Day - (int)first.DayOfWeek + 7) % 7;
        var firstMatchDay = first.DayOfYear + firstOffset;
        var lastOffset = ((int)last.DayOfWeek - (int)specifier.Day + 7) % 7;
        var lastMatchDay = last.DayOfYear - lastOffset;
        var ordinalFromStart = (date.DayOfYear - firstMatchDay) / 7 + 1;
        var ordinalFromEnd = -((lastMatchDay - date.DayOfYear) / 7 + 1);
        return ordinal > 0 ? ordinalFromStart == ordinal : ordinalFromEnd == ordinal;
    }

    private static DateTime StartOfWeek(DateTime date, DayOfWeek weekStart)
        => date.AddDays(-(((int)date.DayOfWeek - (int)weekStart + 7) % 7));

    private static DateTime WeekYearStart(int year, DayOfWeek weekStart)
        => StartOfWeek(new DateTime(year, 1, 4), weekStart);

    private static (int Year, int Number) GetWeekNumber(DateTime date, DayOfWeek weekStart)
    {
        var weekYear = date.Year;
        var first = WeekYearStart(weekYear, weekStart);
        if (date < first)
        {
            weekYear--;
            first = WeekYearStart(weekYear, weekStart);
        }
        else if (date >= WeekYearStart(weekYear + 1, weekStart))
        {
            weekYear++;
            first = WeekYearStart(weekYear, weekStart);
        }
        var week = (int)((StartOfWeek(date, weekStart) - first).TotalDays / 7) + 1;
        return (weekYear, week);
    }

    private static bool IsAfterUntil(DateTime candidateLocal, DateTimeOffset candidateUtc, UntilBound? until)
    {
        if (until is null) return false;
        if (until.Utc is { } utc) return candidateUtc > utc;
        return until.Local is { } local && candidateLocal > local;
    }

    private static bool TryToUtc(DateTime local, out DateTimeOffset utc)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        utc = new DateTimeOffset(local, offset).ToUniversalTime();
        return true;
    }
}
