using System.Globalization;

namespace ADHDWindows.Core.Quests;

/// <summary>Прогресс задания текстом: «45/200» или для хронометража «3:20/10:00».</summary>
public static class QuestFormat
{
    public static string Progress(QuestView quest) =>
        $"{Amount(Math.Min(quest.Progress, quest.Goal), quest.Unit)}/{Amount(quest.Goal, quest.Unit)}";

    public static string Amount(double value, QuestUnit unit)
    {
        if (unit != QuestUnit.Seconds)
        {
            return Math.Floor(value).ToString(CultureInfo.InvariantCulture);
        }

        var seconds = (long)Math.Floor(value);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>Сколько осталось до смены задания: «12:34» или «1:02:03».</summary>
    public static string Remaining(TimeSpan left)
    {
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        var seconds = (long)Math.Ceiling(left.TotalSeconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }
}
