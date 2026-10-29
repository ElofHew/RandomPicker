using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 名单的读取与抽选。
/// </summary>
/// <remarks>
/// 名单就是一个纯文本文件，一行一个名字。没有引号、没有 JSON、没有转义，
/// 用记事本就能改。空行和以 <c>#</c> 开头的行会被忽略，重复的名字只算一个。
/// </remarks>
public class RosterService
{
    private readonly string _rosterPath;
    private FileSystemWatcher? _watcher;
    private List<string> _names = new();

    public RosterService(string rosterPath)
    {
        _rosterPath = rosterPath;
        EnsureRosterFile();
        Reload();
        StartWatching();
    }

    /// <summary>名单文件的完整路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>当前名单。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>名单文件发生变化并重新载入后触发。</summary>
    public event EventHandler? RosterChanged;

    private void EnsureRosterFile()
    {
        if (File.Exists(_rosterPath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(_rosterPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 首次运行给一份带说明的示例，用户直接往下写名字就行。
        File.WriteAllText(_rosterPath, string.Join(Environment.NewLine,
        [
            "# 一行一个名字，保存后自动生效。",
            "# 以 # 开头的行和空行会被忽略。",
            "",
            "张三",
            "李四",
            "王五"
        ]), new UTF8Encoding(false));
    }

    /// <summary>
    /// 重新读取名单文件。
    /// </summary>
    public void Reload()
    {
        try
        {
            var lines = File.Exists(_rosterPath)
                ? File.ReadAllLines(_rosterPath, Encoding.UTF8)
                : [];

            _names = lines
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith('#'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (IOException)
        {
            // 多半是保存的瞬间被占用了，保留上一次的名单，下一次变更再读。
        }
    }

    private void StartWatching()
    {
        var dir = Path.GetDirectoryName(_rosterPath);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_rosterPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
        }
        catch (Exception)
        {
            // 监视不了就算了，右键菜单里还有「重新载入名单」。
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 记事本保存会连着触发好几次，稍等一下再读，顺便避开写入未完成的时刻。
        System.Threading.Thread.Sleep(120);
        Reload();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 抽一个人。
    /// </summary>
    /// <param name="settings">插件设置，抽选状态（本轮已抽、上次抽到谁）会就地更新。</param>
    /// <returns>抽中的名字；名单为空时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>，每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// </remarks>
    /// <summary>抽一个人。等价于 <see cref="PickMany"/> 抽 1 个。</summary>
    public string? Pick(PickerSettings settings) => PickMany(settings, 1).FirstOrDefault();

    /// <summary>
    /// 一次抽 <paramref name="count"/> 个人，<b>这一批里互不重复</b>。
    /// </summary>
    /// <param name="settings">插件设置，抽选状态（本轮已抽、最近抽过谁）会就地更新。</param>
    /// <param name="count">要抽几个。比名单还多时按名单长度截断。</param>
    /// <returns>抽中的名字；名单为空时返回空表。</returns>
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>，每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// <para/>
    /// 两种模式口径不同：
    /// <list type="bullet">
    /// <item><b>不放回</b>：本轮没抽过的人里等概率挑，抽完一轮自动开新一轮；</item>
    /// <item><b>放回</b>：每次都从全体里挑，但按「最近抽过」降权——见 <see cref="WeightedPicker"/>。</item>
    /// </list>
    /// 不管哪种模式，同一批里都不会重复。
    /// </remarks>
    public List<string> PickMany(PickerSettings settings, int count)
    {
        var picked = new List<string>();
        if (_names.Count == 0 || count <= 0)
        {
            return picked;
        }

        // 一次抽不了比名单还多的人。
        count = Math.Min(count, _names.Count);

        if (settings.IsNoRepeat)
        {
            PickWithoutReplacement(settings, count, picked);
        }
        else
        {
            PickWithReplacement(settings, count, picked);
        }

        if (picked.Count > 0)
        {
            // 最近抽过谁只记在抽完之后：一次抽三个，那三个都算「刚抽过」。
            settings.LastPicked = picked[^1];
            foreach (var name in picked)
            {
                WeightedPicker.Remember(settings.RecentPicks, name);
            }
        }

        return picked;
    }

    /// <summary>不放回：本轮没抽过的人里等概率挑。</summary>
    private void PickWithoutReplacement(PickerSettings settings, int count, List<string> picked)
    {
        var remaining = Undrawn(settings);

        for (var i = 0; i < count; i++)
        {
            if (remaining.Count == 0)
            {
                // 一轮抽完了：开新一轮。这一批已经抽到的仍然排除在外。
                remaining = StartNewRound(settings, picked);
                if (remaining.Count == 0)
                {
                    break;
                }
            }

            var chosen = remaining[RandomNumberGenerator.GetInt32(remaining.Count)];
            picked.Add(chosen);
            remaining.Remove(chosen);
            settings.DrawnThisRound.Add(chosen);
        }
    }

    /// <summary>放回：每次都从全体里挑，按「最近抽过」降权，同一批里不重复。</summary>
    private void PickWithReplacement(PickerSettings settings, int count, List<string> picked)
    {
        for (var i = 0; i < count; i++)
        {
            var pool = _names.Where(x => !picked.Contains(x)).ToList();
            if (pool.Count == 0)
            {
                break;
            }

            // 上一次抽到的人权重是 0，<see cref="WeightedPicker"/> 内部自然会避开他，
            // 所以这里不需要再单独把他剔出去。
            picked.Add(WeightedPicker.Pick(pool, settings.RecentPicks));
        }
    }

    /// <summary>本轮还没抽到的人。</summary>
    private List<string> Undrawn(PickerSettings settings) =>
        _names.Where(x => !settings.DrawnThisRound.Contains(x, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// 开新一轮：清空本轮记录，并把上一轮最后一个人挡在门外。
    /// </summary>
    /// <remarks>
    /// 「挡一下」是为了观感：上一轮最后一抽和这一轮第一抽是紧挨着的，
    /// 撞上就像抽重了。名单只剩一个人时挡不住，也没必要挡。
    /// </remarks>
    private List<string> StartNewRound(PickerSettings settings, List<string> picked)
    {
        settings.DrawnThisRound.Clear();

        var remaining = _names.Where(x => !picked.Contains(x)).ToList();

        if (remaining.Count > 1 && settings.LastPicked is { } last)
        {
            remaining.RemoveAll(x => string.Equals(x, last, StringComparison.Ordinal));
        }

        return remaining;
    }

    /// <summary>「不重复」模式下手动开始新一轮。</summary>
    public static void ResetRound(PickerSettings settings)
    {
        settings.DrawnThisRound.Clear();
    }

    /// <summary>本轮还剩多少人没抽到。</summary>
    public int RemainingInRound(PickerSettings settings)
    {
        if (!settings.IsNoRepeat)
        {
            return _names.Count;
        }

        return Math.Max(0, _names.Count - _names.Count(x =>
            settings.DrawnThisRound.Contains(x, StringComparer.Ordinal)));
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileTouched;
        _watcher.Created -= OnFileTouched;
        _watcher.Renamed -= OnFileTouched;
        _watcher.Dispose();
        _watcher = null;
    }
}
