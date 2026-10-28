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
    /// <param name="settings">插件设置，抽选状态（本轮已抽、上次抽到谁）会就地更新。</param>
    /// <param name="count">要抽几个。比名单还多时按名单长度截断。</param>
    /// <returns>抽中的名字；名单为空时返回空表。</returns>
    /// <remarks>
    /// 随机数走 <see cref="RandomNumberGenerator"/>，每次取值都直接来自操作系统的熵源，
    /// 不存在「用时间戳当种子、同一毫秒内连抽拿到同一个结果」这类问题。
    /// <para/>
    /// 不重复模式下，抽完一轮会自动清空重来——连续抽 3 个人时可能横跨轮次的边界，
    /// 这时候同一批里<b>仍然不会重复</b>（已经抽到的人会一直被排除）。
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

        var noRepeat = settings.IsNoRepeat;

        // 这一批里已经拿过的人。初始还要带上之前的状态：
        // 不重复模式下是本轮已抽的，另外两种模式都回避「和上一个人重样」。
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (noRepeat)
        {
            used.UnionWith(settings.DrawnThisRound);
        }

        if (settings.LastPicked is { } last)
        {
            used.Add(last);
        }

        for (var i = 0; i < count; i++)
        {
            var pool = _names.Where(x => !used.Contains(x)).ToList();
            if (pool.Count == 0)
            {
                // 一轮抽完了：清掉本轮记录，从全体里再来。这一批已经抽到的仍然排除在外。
                settings.DrawnThisRound.Clear();
                used.Clear();
                foreach (var already in picked)
                {
                    used.Add(already);
                }

                pool = _names.Where(x => !used.Contains(x)).ToList();
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
                settings.DrawnThisRound.Add(chosen);
            }

            settings.LastPicked = chosen;
        }

        return picked;
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
