using System.Text.Json;
using System.Text.RegularExpressions;
using ADHDWindows.Core.Data;
using ADHDWindows.Core.Logging;

namespace ADHDWindows.Core.Upgrades;

/// <summary>
/// Читает ветки улучшений из файлов data/upgrades/*.json. Неверная запись пропускается с ошибкой в журнале
/// и не мешает остальным. Вместе с ней пропускаются записи, которые от неё зависят.
/// </summary>
public static partial class UpgradeCatalog
{
    public const string DataFolder = "upgrades/";
    public const int LevelLimit = 1000;

    public static IReadOnlyList<UpgradeBranch> Load(GameData data, ILog log)
    {
        var files = new List<(string Key, BranchFile File)>();
        foreach (var key in data.Keys.Where(key => key.StartsWith(DataFolder, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            if (data.TryGet<BranchFile>(key, out var file) && file is not null)
            {
                files.Add((key, file));
            }
        }

        return Build(files, log);
    }

    internal static IReadOnlyList<UpgradeBranch> Build(IReadOnlyList<(string Key, BranchFile File)> files, ILog log)
    {
        var branches = new List<(string Id, string Name, string Icon, int Order)>();
        var upgrades = new Dictionary<string, UpgradeDefinition>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (key, file) in files)
        {
            var info = file.Branch;
            var branchId = info?.Id ?? key[DataFolder.Length..];
            if (!IsCode(branchId) || string.IsNullOrWhiteSpace(info?.Name))
            {
                log.Error($"Ветка улучшений {key} пропущена: нужны код и название");
                continue;
            }

            if (branches.Exists(branch => branch.Id == branchId))
            {
                log.Error($"Ветка улучшений {key} пропущена: код {branchId} уже занят");
                continue;
            }

            branches.Add((branchId, info.Name.Trim(), info.Icon ?? "star", info.Order));
            foreach (var record in file.Upgrades ?? [])
            {
                if (Parse(record, branchId, out var problem) is not { } upgrade)
                {
                    log.Error($"Улучшение {record?.Id ?? "без кода"} в {key} пропущено: {problem}");
                }
                else if (!upgrades.TryAdd(upgrade.Id, upgrade))
                {
                    log.Error($"Улучшение {upgrade.Id} в {key} пропущено: код уже занят");
                }
                else
                {
                    order.Add(upgrade.Id);
                }
            }
        }

        DropBrokenRequirements(upgrades, log);
        DropCycles(upgrades, log);

        return branches
            .OrderBy(branch => branch.Order)
            .Select(branch => new UpgradeBranch(
                branch.Id,
                branch.Name,
                branch.Icon,
                branch.Order,
                order.Where(upgrades.ContainsKey).Select(id => upgrades[id]).Where(upgrade => upgrade.Branch == branch.Id).ToList()))
            .ToList();
    }

    private static UpgradeDefinition? Parse(UpgradeRecord? record, string branch, out string problem)
    {
        problem = string.Empty;
        if (record is null || !IsCode(record.Id))
        {
            problem = "код должен состоять из латинских букв, цифр, точек, дефисов и подчёркиваний";
            return null;
        }

        if (string.IsNullOrWhiteSpace(record.Name) || string.IsNullOrWhiteSpace(record.Effect))
        {
            problem = "нужны название и эффект";
            return null;
        }

        if (record.Price < 0 || record.Growth < 1 || record.MaxLevel is < 1 or > LevelLimit)
        {
            problem = $"цена не меньше 0, рост цены не меньше 1, уровней от 1 до {LevelLimit}";
            return null;
        }

        var requires = record.Requires ?? [];
        if (requires.Any(pair => !IsCode(pair.Key) || pair.Value < 1 || pair.Key == record.Id))
        {
            problem = "требования должны ссылаться на другие улучшения с уровнем от 1";
            return null;
        }

        return new UpgradeDefinition(
            record.Id!,
            branch,
            record.Name.Trim(),
            record.Description?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(record.Icon) ? "arrow_up" : record.Icon,
            record.Effect.Trim(),
            record.Params ?? [],
            record.Price,
            record.Growth,
            record.MaxLevel,
            requires);
    }

    /// <summary>Требование к отсутствующему улучшению или к уровню выше максимального не выполнить никогда.</summary>
    private static void DropBrokenRequirements(Dictionary<string, UpgradeDefinition> upgrades, ILog log)
    {
        bool dropped;
        do
        {
            dropped = false;
            foreach (var upgrade in upgrades.Values.ToList())
            {
                var broken = upgrade.Requires.FirstOrDefault(pair =>
                    !upgrades.TryGetValue(pair.Key, out var required) || pair.Value > required.MaxLevel);
                if (broken.Key is not null)
                {
                    log.Error($"Улучшение {upgrade.Id} пропущено: требование {broken.Key} ур. {broken.Value} невыполнимо");
                    upgrades.Remove(upgrade.Id);
                    dropped = true;
                }
            }
        }
        while (dropped);
    }

    private static void DropCycles(Dictionary<string, UpgradeDefinition> upgrades, ILog log)
    {
        // 0 — не посещено, 1 — в обходе, 2 — проверено.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        bool Visit(string id)
        {
            switch (state.GetValueOrDefault(id))
            {
                case 1:
                    return true;
                case 2:
                    return cyclic.Contains(id);
            }

            state[id] = 1;
            var inCycle = false;
            foreach (var required in upgrades[id].Requires.Keys)
            {
                inCycle |= Visit(required);
            }

            state[id] = 2;
            if (inCycle)
            {
                cyclic.Add(id);
            }

            return inCycle;
        }

        foreach (var id in upgrades.Keys.ToList())
        {
            Visit(id);
        }

        foreach (var id in cyclic)
        {
            log.Error($"Улучшение {id} пропущено: требования образуют круг");
            upgrades.Remove(id);
        }
    }

    private static bool IsCode(string? value) => value is not null && CodePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*$")]
    private static partial Regex CodePattern();

    internal sealed record BranchFile(BranchInfo? Branch, List<UpgradeRecord?>? Upgrades);

    internal sealed record BranchInfo(string? Id, string? Name, string? Icon, int Order = 0);

    internal sealed record UpgradeRecord(
        string? Id,
        string? Name,
        string? Description,
        string? Icon,
        string? Effect,
        Dictionary<string, JsonElement>? Params,
        Dictionary<string, int>? Requires,
        long Price = 0,
        decimal Growth = 1,
        int MaxLevel = 1);
}
