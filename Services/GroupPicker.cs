using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 抽组：从「A 组 / B 组 / …」或者「1 组 / 2 组 / …」里随机挑一个。
/// </summary>
/// <remarks>
/// 组不是名单——它由 <see cref="PickerSettings.GroupCount"/> 和
/// <see cref="PickerSettings.GroupStyle"/> 直接算出来，不读文件，所以改了设置立刻生效。
/// <para/>
/// 抽选逻辑跟 <see cref="RosterService.PickMany"/> 是一套：同一批内不重复，
/// 不放回时跨批次也不重复，抽完一轮自动开新一轮。
/// </remarks>
public static class GroupPicker
{
    /// <summary>
    /// 按当前设置列出所有组的标签，顺序就是 A、B、C…… 或 1、2、3……
    /// </summary>
    public static List<string> Labels(PickerSettings settings)
    {
        var count = Math.Clamp(settings.GroupCount, 1, PickerSettings.MaxGroups);
        var labels = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            labels.Add(settings.GroupStyle == GroupStyle.Letter
                ? $"{(char)('A' + i)} 组"
                : $"{i + 1} 组");
        }

        return labels;
    }

    /// <summary>
    /// 一次抽 <paramref name="count"/> 个组，<b>这一批里互不重复</b>。
    /// </summary>
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>：每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// </remarks>
    public static List<string> PickMany(PickerSettings settings, int count)
    {
        var picked = new List<string>();
        var labels = Labels(settings);
        if (labels.Count == 0 || count <= 0)
        {
            return picked;
        }

        // 一次抽不了比组数还多的组。
        count = Math.Min(count, labels.Count);

        var noRepeat = settings.IsNoRepeat;

        var used = new HashSet<string>(StringComparer.Ordinal);
        if (noRepeat)
        {
            used.UnionWith(settings.DrawnGroupsThisRound);
        }

        if (settings.LastGroupPicked is { } last)
        {
            used.Add(last);
        }

        for (var i = 0; i < count; i++)
        {
            var pool = labels.Where(x => !used.Contains(x)).ToList();
            if (pool.Count == 0)
            {
                // 一轮抽完了：清掉本轮记录，从全体里再来。这一批已经抽到的仍然排除在外。
                settings.DrawnGroupsThisRound.Clear();
                used.Clear();
                foreach (var already in picked)
                {
                    used.Add(already);
                }

                pool = labels.Where(x => !used.Contains(x)).ToList();
                if (pool.Count == 0)
                {
                    break;
                }
            }

            var chosen = pool[RandomNumberGenerator.GetInt32(pool.Count)];
            picked.Add(chosen);
            used.Add(chosen);

            if (noRepeat)
            {
                settings.DrawnGroupsThisRound.Add(chosen);
            }

            settings.LastGroupPicked = chosen;
        }

        return picked;
    }

    /// <summary>本轮还剩几组没抽到。放回模式下没有轮次概念，等于总组数。</summary>
    public static int RemainingInRound(PickerSettings settings)
    {
        var labels = Labels(settings);
        if (!settings.IsNoRepeat)
        {
            return labels.Count;
        }

        return Math.Max(0, labels.Count - labels.Count(x =>
            settings.DrawnGroupsThisRound.Contains(x, StringComparer.Ordinal)));
    }

    /// <summary>手动开始新一轮。</summary>
    /// <remarks>
    /// 改了组数或组名写法之后也应该调一次——旧的「E 组」记录留着没有意义。
    /// </remarks>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnGroupsThisRound.Clear();
        settings.LastGroupPicked = null;
    }
}
