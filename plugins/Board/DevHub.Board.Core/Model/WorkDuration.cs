using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DevHub.Board.Core.Model;

/// <summary>
/// Jira-style durations for time tracking: "1w 2d 4h 30m". Stored as minutes; a day and a week are working
/// time (<see cref="ProjectSettings.HoursPerDay"/>, <see cref="ProjectSettings.DaysPerWeek"/>), not calendar time.
/// </summary>
public static partial class WorkDuration
{
    [GeneratedRegex(@"^\s*(?:(?<n>\d+(?:[.,]\d+)?)\s*(?<u>w|d|h|m)\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    /// <summary>
    /// Parses "2h 30m", "1.5d", "3w", or a bare number of hours ("4" = 4h). False for anything else or zero.
    /// </summary>
    public static bool TryParse(string? text, ProjectSettings settings, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
        {
            minutes = (int)Math.Round(hours * 60);
            return minutes > 0;
        }

        var match = DurationPattern().Match(trimmed);
        if (!match.Success)
            return false;

        double total = 0;
        var numbers = match.Groups["n"].Captures;
        var units = match.Groups["u"].Captures;
        for (var i = 0; i < numbers.Count; i++)
        {
            var value = double.Parse(numbers[i].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            total += char.ToLowerInvariant(units[i].Value[0]) switch
            {
                'w' => value * settings.DaysPerWeek * settings.HoursPerDay * 60,
                'd' => value * settings.HoursPerDay * 60,
                'h' => value * 60,
                _ => value,
            };
        }
        minutes = (int)Math.Round(total);
        return minutes > 0;
    }

    /// <summary>"1w 2d 4h 30m" (largest units first, zero parts left out); "0m" for zero.</summary>
    public static string Format(int minutes, ProjectSettings settings)
    {
        if (minutes <= 0)
            return "0m";

        var day = (int)Math.Round(settings.HoursPerDay * 60);
        var week = (int)Math.Round(settings.DaysPerWeek * day);
        var text = new StringBuilder();
        Append(ref minutes, week, 'w');
        Append(ref minutes, day, 'd');
        Append(ref minutes, 60, 'h');
        Append(ref minutes, 1, 'm');
        return text.ToString();

        void Append(ref int left, int unit, char suffix)
        {
            if (unit <= 0 || left < unit)
                return;
            if (text.Length > 0)
                text.Append(' ');
            text.Append(left / unit).Append(suffix);
            left %= unit;
        }
    }
}
