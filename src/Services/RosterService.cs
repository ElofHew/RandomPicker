using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassIsland.RandomPicker.Models;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 名单的读取与抽选。
/// </summary>
/// <remarks>
/// 名单就是一个纯文本文件，一行一个名字。没有引号、没有 JSON、没有转义，
/// 用记事本就能改。空行和以 <c>#</c> 开头的行会被忽略，重复的名字只算一个。
/// <para/>
/// 还有一种特殊行：<c>@高一5班</c> —— 给这份名单起个名字，用在菜单和设置页上。
/// 它不算人名，也不会被抽到。
/// <para/>
/// 以 <c>%</c> 开头的是「幽灵人」：<c>%张三</c> 计入人数，但在
/// <see cref="PickerSettings.GhostMode"/> 开着时永远抽不到。前缀只是文件里的记号，
/// 存进名单时已经去掉，界面上看到的就是「张三」。
/// </remarks>
public class RosterService
{
    /// <summary>起名行的开头，后面直接跟名字。</summary>
    private const char TitleStart = '@';

    /// <summary>幽灵人的记号，后面直接跟名字。</summary>
    private const char GhostStart = '%';

    private string _rosterPath;
    private FileSystemWatcher? _watcher;
    private List<string> _names = new();

    /// <summary>哪些名字是从 <c>%</c> 行来的。存的是去掉前缀之后的名字。</summary>
    private readonly HashSet<string> _ghosts = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>except.json</c> 的路径。为空表示这份名单不吃外部排除表。
    /// </summary>
    private readonly string? _exceptPath;

    /// <summary>这套排除规则是从 <c>except.json</c> 里来的名字数量（不含 <c>%</c> 行）。</summary>
    private int _exceptGhostCount;

    public RosterService(string rosterPath, string? exceptPath = null)
    {
        _rosterPath = rosterPath;
        _exceptPath = exceptPath;
        EnsureRosterFile();
        Reload();
        StartWatching();
    }

    /// <summary>名单文件的完整路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>当前名单。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>名单里有几个幽灵人（<c>%</c> 开头的那几行）。</summary>
    public int GhostCount => _ghosts.Count;

    /// <summary>
    /// 其中来自 <c>except.json</c> 的有几个。
    /// </summary>
    /// <remarks>名单文件里看不到这些人，所以设置页要单独报一下，否则用户会以为文件被改了。</remarks>
    public int ExceptGhostCount => _exceptGhostCount;

    /// <summary>名字是不是幽灵人。判断的是去掉前缀之后的名字。</summary>
    public bool IsGhost(string name) => _ghosts.Contains(name);

    /// <summary>按当前设置，这个人能不能抽。</summary>
    /// <remarks>幽灵人只在开关打开时排除；关着的时候它们就是普通人。</remarks>
    private bool CanPick(string name, PickerSettings settings) =>
        !settings.GhostMode || !_ghosts.Contains(name);

    /// <summary>按当前设置，实际可抽的人数。幽灵人被排除时比 <see cref="Names"/> 少。</summary>
    public int PickableCount(PickerSettings settings) => _names.Count(x => CanPick(x, settings));

    /// <summary>名单自己声明的名字（<c>@…</c> 那一行）。没写过就是 <c>null</c>。</summary>
    public string? Title { get; private set; }

    /// <summary>名单文件发生变化并重新载入后触发。</summary>
    public event EventHandler? RosterChanged;

    /// <summary>
    /// 换一份名单文件，还是这个实例。
    /// </summary>
    /// <remarks>
    /// 不新建实例是因为悬浮球和设置页都订阅着 <see cref="RosterChanged"/>，
    /// 换实例等于把订阅全弄丢，还得让每个订阅方都重新挂一遍。
    /// </remarks>
    public void SwitchTo(string rosterPath)
    {
        if (string.Equals(_rosterPath, rosterPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopWatching();
        _rosterPath = rosterPath;
        EnsureRosterFile();
        Reload();
        StartWatching();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 文件不在、或者文件在但一个名字都没有时，写一份带说明的示例进去。
    /// </summary>
    /// <remarks>
    /// 只在构造函数和 <see cref="SwitchTo"/> 里调，也就是「刚打开这份名单」时。
    /// 运行中把名字全删了不会马上被填回来——那是用户自己的操作，
    /// 立刻覆盖掉会让人以为编辑器没保存成功。
    /// </remarks>
    private void EnsureRosterFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_rosterPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(_rosterPath) && HasAnyName())
            {
                return;
            }

            File.WriteAllText(_rosterPath, Template, new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 写不进去就算了，后面 Reload 读到空名单，球上会显示「空」。
        }
    }

    /// <summary>文件里有没有至少一个能抽的名字。</summary>
    private bool HasAnyName()
    {
        foreach (var raw in File.ReadAllLines(_rosterPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (TryParseTitle(line, out _) || line.StartsWith(TitleStart))
            {
                continue;
            }

            // 光一个 % 不算名字，别让一份只有它的文件看着「有内容」。
            if (line.StartsWith(GhostStart) && line[1..].Trim().Length == 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 首次运行给的示例。十个人是为了让人一眼看出「格式就是这样，往下加」，
    /// 三个人的话容易以为是写死的。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Environment.NewLine"/> 拼，落地就是 CRLF——
    /// 老版本记事本只认 CRLF，写成 LF 会挤成一整行。
    /// </remarks>
    private static readonly string Template = string.Join(Environment.NewLine,
    [
        "# 一行一个名字，保存后自动生效。",
        "# 以 # 开头的行和空行会被忽略。",
        "# 想给这份名单起个名字，在开头写一行：@高一5班",
        "",
        "张三",
        "李四",
        "王五",
        "赵六",
        "钱七",
        "孙八",
        "周九",
        "吴十",
        "郑十一",
        "王十二"
    ]);

    /// <summary>
    /// 重读文件，并且把文件监视器整个重开一遍。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="Reload"/> 的区别在这里：监视器有可能已经死了（网络盘断开、
    /// 文件被别的程序独占过、目录被临时删掉又建回来），那种情况下光是重读，
    /// 之后改文件就再也不会有反应。「刷新插件状态」走的是这一条。
    /// </remarks>
    public void Refresh()
    {
        StopWatching();
        Reload();
        StartWatching();
        RosterChanged?.Invoke(this, EventArgs.Empty);
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

            string? title = null;

            // 先按行收，最后再统一去重：同名的一行带 %、一行不带时，要以「不带」为准
            // （他既然后面写了真名，就说明这个人是要被抽的）。
            var entries = new List<(string Name, bool Ghost)>();

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (TryParseTitle(line, out var parsed))
                {
                    // 名字只认第一处，后面重复写的当普通行忽略掉。
                    title ??= parsed;
                    continue;
                }

                // 以 @ 开头却解析不出名字的，是想写起名行但写坏了。
                // 当成人名留在名单里没好处——真被抽出来就是个古怪的字符串。
                if (line.StartsWith(TitleStart))
                {
                    continue;
                }

                if (line.StartsWith(GhostStart))
                {
                    // 光一个 % 后面什么都没有，算写坏了，不留。
                    var ghost = line[1..].Trim();
                    if (ghost.Length > 0)
                    {
                        entries.Add((ghost, true));
                    }

                    continue;
                }

                entries.Add((line, false));
            }

            Title = title;
            _names = entries.Select(x => x.Name).Distinct(StringComparer.Ordinal).ToList();

            _ghosts.Clear();
            var real = new HashSet<string>(
                entries.Where(x => !x.Ghost).Select(x => x.Name), StringComparer.Ordinal);
            foreach (var (name, ghost) in entries)
            {
                if (ghost && !real.Contains(name))
                {
                    _ghosts.Add(name);
                }
            }

            // 外部排除表盖在 % 之上：这是用户专门写下来要排除的人，
            // 就算名单里同时有一行不带 % 的真名，也按排除算。
            _exceptGhostCount = 0;
            foreach (var name in LoadExclusions(_exceptPath, _rosterPath, title))
            {
                if (_names.Contains(name, StringComparer.Ordinal) && _ghosts.Add(name))
                {
                    _exceptGhostCount++;
                }
            }
        }
        catch (IOException)
        {
            // 多半是保存的瞬间被占用了，保留上一次的名单，下一次变更再读。
        }
    }

    /// <summary>
    /// 读 <c>except.json</c>，挑出跟这份名单有关的那份排除名单。
    /// </summary>
    /// <param name="exceptPath">文件位置，为空或不存在都当没配。</param>
    /// <param name="rosterPath">当前名单文件，用来按文件名匹配。</param>
    /// <param name="title">名单自己声明的名字，用来按名字匹配。</param>
    /// <remarks>
    /// 键可以是文件名（<c>list2.txt</c> / 不带后缀的 <c>list2</c>），也可以是名单名（<c>高一5班</c>）。
    /// 两种都认，是因为人有两种记法：没起名的名单只有文件名可写，起了名的用名字更直观。
    /// <para/>
    /// 键和名字都按不区分大小写比：这是配置，不是内容，写错大小写不该静默失效。
    /// 文件读坏了就当没配，不能让一段坏 JSON 把名单也搞没。
    /// </remarks>
    private static HashSet<string> LoadExclusions(string? exceptPath, string rosterPath, string? title)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(exceptPath) || !File.Exists(exceptPath))
        {
            return excluded;
        }

        Dictionary<string, List<string>>? table;
        try
        {
            table = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(exceptPath), ExceptJsonOptions);
        }
        catch (Exception)
        {
            // 坏了、写了一半、被独占，都当没配。下次刷新插件状态会重读。
            return excluded;
        }

        if (table is null)
        {
            return excluded;
        }

        var file = Path.GetFileName(rosterPath);
        var stem = Path.GetFileNameWithoutExtension(rosterPath);

        foreach (var (key, names) in table)
        {
            if (names is null || !KeyMatches(key, file, stem, title))
            {
                continue;
            }

            foreach (var raw in names)
            {
                var name = raw?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    excluded.Add(name);
                }
            }
        }

        return excluded;
    }

    private static bool KeyMatches(string key, string file, string stem, string? title)
    {
        var k = key.Trim();
        if (k.Length == 0)
        {
            return false;
        }

        return string.Equals(k, file, StringComparison.OrdinalIgnoreCase)
               || string.Equals(k, stem, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(title) &&
                   string.Equals(k, title.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>except.json</c> 的读法：允许注释和末尾逗号，人手写的东西别太苛刻。</summary>
    private static readonly JsonSerializerOptions ExceptJsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// 认一下这一行是不是「给名单起名」：<c>@名字</c>。
    /// </summary>
    /// <remarks>
    /// <c>@</c> 后面紧跟的空格会自动去掉（<c>@ 高一5班</c> 也认），
    /// 手滑多敲一个空格不至于让名字消失；名字两端也 Trim。
    /// 光一个 <c>@</c>、后面什么都没有的算写坏了——返回 false，
    /// 调用方会把整行跳过，不会当成名字抽出来。
    /// </remarks>
    private static bool TryParseTitle(string line, out string title)
    {
        title = string.Empty;

        if (!line.StartsWith(TitleStart))
        {
            return false;
        }

        title = line[1..].Trim();
        return title.Length > 0;
    }

    /// <summary>
    /// 只读出名单名，不建实例。
    /// </summary>
    /// <remarks>菜单里一次要问五份名单，为这个各建一个带文件监视的实例太重了。</remarks>
    public static string? PeekTitle(string rosterPath)
    {
        try
        {
            if (!File.Exists(rosterPath))
            {
                return null;
            }

            foreach (var raw in File.ReadAllLines(rosterPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (TryParseTitle(line, out var title))
                {
                    return title;
                }
            }
        }
        catch (IOException)
        {
            // 读不出来就当没名字。
        }

        return null;
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
            // 监视不了就算了，右键菜单里还有「重新加载名单」。
        }
    }

    private void StopWatching()
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

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 记事本保存会连着触发好几次，稍等一下再读，顺便避开写入未完成的时刻。
        System.Threading.Thread.Sleep(120);
        Reload();
        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

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

        // 一次抽不了比能抽的人还多。幽灵人算进总人数，但不在这一批的范围里。
        count = Math.Min(count, PickableCount(settings));
        if (count <= 0)
        {
            return picked;
        }

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
            var pool = _names
                .Where(x => CanPick(x, settings) && !picked.Contains(x))
                .ToList();
            if (pool.Count == 0)
            {
                break;
            }

            // 上一次抽到的人权重是 0，<see cref="WeightedPicker"/> 内部自然会避开他，
            // 所以这里不需要再单独把他剔出去。
            picked.Add(WeightedPicker.Pick(pool, settings.RecentPicks));
        }
    }

    /// <summary>本轮还没抽到的人。幽灵人不算——它们本来就不参与轮次。</summary>
    private List<string> Undrawn(PickerSettings settings) =>
        _names.Where(x => CanPick(x, settings) &&
                          !settings.DrawnThisRound.Contains(x, StringComparer.Ordinal))
              .ToList();

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

        var remaining = _names
            .Where(x => CanPick(x, settings) && !picked.Contains(x))
            .ToList();

        if (remaining.Count > 1 && settings.LastPicked is { } last)
        {
            remaining.RemoveAll(x => string.Equals(x, last, StringComparison.Ordinal));
        }

        return remaining;
    }

    /// <summary>
    /// 本轮还剩多少人没抽到。
    /// </summary>
    /// <remarks>
    /// <b>幽灵人算在里面。</b>它们本来就是为了让总数看起来没动过，
    /// 这里要是把它们减掉，球上开局就会显示「25/30」，比不藏还明显。
    /// 代价是全部真人抽完之后还剩几个永远抽不到的，得靠「重置本轮」或下一次自动开新轮。
    /// </remarks>
    public int RemainingInRound(PickerSettings settings)
    {
        if (!settings.IsNoRepeat)
        {
            return _names.Count;
        }

        return Math.Max(0, _names.Count - _names.Count(x =>
            settings.DrawnThisRound.Contains(x, StringComparer.Ordinal)));
    }

    public void Dispose() => StopWatching();
}
