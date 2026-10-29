using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.RandomPicker.Models;
using ClassIsland.RandomPicker.Views;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.RandomPicker.Services;

/// <summary>
/// 插件主体：管住悬浮球、设置、名单和小组，以及抽中之后往哪儿显示。
/// </summary>
public class PickerHostService : IHostedService
{
    private readonly string _settingsPath;
    private readonly string _rosterPath;
    private readonly string _configFolder;

    /// <summary>拍照抽人正在跑。摄像头开一次要一两秒，这期间再点就直接忽略。</summary>
    private bool _shooting;

    private PickerSettings _settings = new();
    private RosterService? _roster;
    private PickerWindow? _window;

    /// <summary>上一条还在播的提醒。连着抽的时候先把它取消掉，免得在主界面上排队堆积。</summary>
    private NotificationRequest? _lastRequest;

    /// <summary>托盘菜单里那条「显示 / 隐藏抽选悬浮球」。</summary>
    private NativeMenuItem? _trayItem;

    /// <summary>托盘菜单还没建好时的重试定时器。</summary>
    private DispatcherTimer? _trayRetryTimer;

    private int _trayRetryCount;

    public PickerHostService(string pluginConfigFolder)
    {
        _configFolder = pluginConfigFolder;
        _settingsPath = Path.Combine(pluginConfigFolder, "settings.json");
        // 名单跟设置放一起。右键菜单里的「打开名单文件」直接把它交给记事本。
        _rosterPath = Path.Combine(pluginConfigFolder, "名单.txt");
    }

    /// <summary>当前设置。设置页直接绑这上面。</summary>
    public PickerSettings Settings => _settings;

    /// <summary>名单文件路径。</summary>
    public string RosterPath => _rosterPath;

    /// <summary>插件配置目录。拍照要存原图时用。</summary>
    public string ConfigFolder => _configFolder;

    /// <summary>插件自己所在的目录。ONNX Runtime 和人脸模型都在这儿。</summary>
    public static string PluginDirectory =>
        Path.GetDirectoryName(typeof(PickerHostService).Assembly.Location) ?? string.Empty;

    /// <summary>把设置存盘。设置页改完调一下。</summary>
    public void SaveSettings() => SaveSettingsInternal();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings = PickerSettings.Load(_settingsPath);
        _roster = new RosterService(_rosterPath);

        // 宿主启动 IHostedService 的时候 Avalonia 主窗口不一定已经就绪，
        // 用 Background 优先级排队，等 UI 空下来再开窗口。
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTrayMenuItem();
            ShowPickerWindow();
        }, DispatcherPriority.Background);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SaveSettingsInternal();
        Dispatcher.UIThread.Post(() =>
        {
            RemoveTrayMenuItem();
            RevealWindow.CloseCurrent();
            _window?.Close();
            _window = null;
        });
        _roster?.Dispose();
        // 摄像头可能还热着，一定要关掉，否则指示灯会一直亮。
        return CameraPicker.DisposeAllAsync();
    }

    private void ShowPickerWindow()
    {
        if (_window is not null || _roster is null)
        {
            return;
        }

        // 上一次是被用户主动藏起来的，别自己冒出来 —— 等托盘菜单或设置页叫回来。
        if (_settings.Hidden)
        {
            return;
        }

        _window = new PickerWindow(_settings, _roster);
        _window.PickRequested += (_, _) => Pick();
        _window.SettingsChanged += (_, _) => SaveSettingsInternal();
        _window.Show();
    }

    #region 显示 / 隐藏

    /// <summary>
    /// 显示或隐藏悬浮窗，并把状态记进设置（会存盘）。
    /// </summary>
    /// <remarks>
    /// 隐藏时<b>不销毁窗口实例</b>，只是 <see cref="Window.Hide()"/>。
    /// 这样再显示回来时位置、置顶定时器、名单状态全都在，不会有「重新出现但挪了地方」的割裂感。
    /// <para/>
    /// 隐藏状态是持久的：重启 ClassIsland 也保持隐藏，不用每次开机再点一次。
    /// <para/>
    /// 托盘菜单那一项的标题见 <see cref="RefreshTrayMenuItem"/>。
    /// </remarks>
    public void SetPickerWindowVisible(bool visible)
    {
        _settings.Hidden = !visible;

        if (visible)
        {
            if (_window is null)
            {
                ShowPickerWindow();
            }
            else if (!_window.IsVisible)
            {
                _window.Show();
            }
        }
        else
        {
            // 中央弹窗是独立实例，悬浮球没了它也没道理继续占着屏幕。
            RevealWindow.CloseCurrent();
            // 先存位置再隐藏：藏起来之后窗口位置可能就取不到有效值了。
            _window?.CapturePosition();
            _window?.Hide();
        }

        SaveSettingsInternal();
        RefreshTrayMenuItem();
    }

    /// <summary>
    /// 往宿主托盘菜单的「更多选项」里加一条「显示 / 隐藏抽选悬浮球」。
    /// </summary>
    /// <remarks>
    /// 悬浮窗藏起来之后就没有任何入口能把它叫回来了，所以必须在宿主托盘菜单里留一条。
    /// <para/>
    /// <see cref="ITaskBarIconService.MoreOptionsMenuItems"/> 背后是 <c>MoreOptionsMenu!.Items</c>，
    /// 而那个菜单要等宿主的 MainWindow 构造完才存在，取一次会直接抛空引用。
    /// 插件有可能比它先起来，所以加不上就隔一秒重试，最多试半分钟。
    /// </remarks>
    private void EnsureTrayMenuItem()
    {
        if (_trayItem is not null)
        {
            return;
        }

        var service = IAppHost.Host?.Services.GetService<ITaskBarIconService>();
        if (service is null)
        {
            return;
        }

        _trayItem = new NativeMenuItem();
        _trayItem.Click += (_, _) => SetPickerWindowVisible(_settings.Hidden);

        if (TryAddTrayMenuItem(service))
        {
            return;
        }

        _trayRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayRetryTimer.Tick += (_, _) =>
        {
            if (TryAddTrayMenuItem(service) || ++_trayRetryCount >= 30)
            {
                _trayRetryTimer?.Stop();
                _trayRetryTimer = null;
            }
        };
        _trayRetryTimer.Start();
    }

    private bool TryAddTrayMenuItem(ITaskBarIconService service)
    {
        if (_trayItem is null)
        {
            return false;
        }

        try
        {
            service.MoreOptionsMenuItems.Add(_trayItem);
            RefreshTrayMenuItem();
            return true;
        }
        catch (Exception)
        {
            // 宿主的「更多选项」菜单还没建好，过一会儿再试。
            return false;
        }
    }

    private void RemoveTrayMenuItem()
    {
        _trayRetryTimer?.Stop();
        _trayRetryTimer = null;

        if (_trayItem is null)
        {
            return;
        }

        try
        {
            IAppHost.Host?.Services.GetService<ITaskBarIconService>()
                ?.MoreOptionsMenuItems.Remove(_trayItem);
        }
        catch (Exception)
        {
            // 宿主已经在关了，菜单没了也无所谓。
        }

        _trayItem = null;
    }

    /// <summary>托盘菜单那一项的文字跟着当前状态走：藏起来了就写「显示抽选悬浮球」。</summary>
    /// <remarks>
    /// 标题里必须带「抽选」两个字。SystemTools 等插件也会往同一个「更多选项」菜单里塞
    /// 「显示悬浮窗 / 隐藏悬浮窗」，那是它们自己的悬浮窗，跟本插件无关；
    /// 两边都叫「隐藏悬浮窗」时用户分不清哪个管哪个。
    /// </remarks>
    private void RefreshTrayMenuItem()
    {
        if (_trayItem is null)
        {
            return;
        }

        _trayItem.Header = _settings.Hidden ? "显示抽选悬浮球" : "隐藏抽选悬浮球";
    }

    #endregion

    #region 设置页

    /// <summary>名单里有多少人。</summary>
    public int RosterCount => _roster?.Names.Count ?? 0;

    /// <summary>本轮还剩多少人没抽到。放回模式下没有轮次概念，等于总人数。</summary>
    public int RemainingInRound => _roster?.RemainingInRound(_settings) ?? 0;

    /// <summary>一共分几个组。由设置里的「小组总数」决定。</summary>
    public int GroupTotal => GroupPicker.Labels(_settings).Count;

    /// <summary>本轮还剩几个组没抽到。放回模式下等于总组数。</summary>
    public int GroupsRemaining => GroupPicker.RemainingInRound(_settings);

    /// <summary>重新读一遍名单文件。</summary>
    public void ReloadRoster()
    {
        _roster?.Reload();
        _window?.RefreshCounter();
    }

    /// <summary>清空本轮记录，提前开始新一轮。名单和小组一起清。</summary>
    public void ResetRound()
    {
        RosterService.ResetRound(_settings);
        GroupPicker.ResetRound(_settings);
        _window?.RefreshCounter();
        SaveSettingsInternal();
    }

    /// <summary>
    /// 把尺寸、人数这些跟悬浮球显示有关的改动刷到球上。
    /// </summary>
    /// <remarks>
    /// 设置页改了档位或抽取模式之后调一下，不然球要等到下次重开才变样子。
    /// </remarks>
    public void RefreshPickerWindow()
    {
        _window?.ApplySize();
        _window?.RefreshCounter();
    }

    #endregion

    private void SaveSettingsInternal()
    {
        _window?.CapturePosition();
        _settings.Save(_settingsPath);
    }

    /// <summary>
    /// 抽一次（人或组），然后按设置决定往哪儿显示。
    /// </summary>
    private void Pick()
    {
        if (_roster is null)
        {
            return;
        }

        if (_settings.Mode == PickMode.Photo)
        {
            // 按设定的概率改成抽名字。掷骰子用密码学随机数，
            // 和抽人用的是同一个源，不会出现「每次开机前几抽都一样」。
            var chance = Math.Clamp(_settings.PhotoTextChance, 0, 100);
            if (chance > 0 && System.Security.Cryptography.RandomNumberGenerator.GetInt32(100) < chance)
            {
                PickFromRoster();
                return;
            }

            PickFromPhoto();
            return;
        }

        if (_settings.Mode == PickMode.Group)
        {
            PickFromGroups();
            return;
        }

        PickFromRoster();
    }

    /// <summary>
    /// 抽组：随机挑一个小组，比如「C 组」。
    /// </summary>
    /// <remarks>
    /// 组不是名单，所以这里既不读文件也不碰人。抽几个、要不要放回，
    /// 跟抽人共用同一套设置（<see cref="PickerSettings.PickCount"/> / <see cref="PickerSettings.IsNoRepeat"/>）。
    /// </remarks>
    private void PickFromGroups()
    {
        var groups = GroupPicker.PickMany(_settings, PickCount);
        SaveSettingsInternal();
        _window?.RefreshCounter();

        if (groups.Count == 0)
        {
            Reveal("没有可抽的小组", isHint: true);
            return;
        }

        if (_settings.ShowCenterReveal)
        {
            Reveal(string.Join('\n', groups), isHint: false);
        }

        if (_settings.ShowNotification)
        {
            SendNotification(string.Join("、", groups));
        }
    }

    /// <summary>
    /// 拍照抽人：开摄像头、拍一张、检测、挑出若干个人裁成人像。
    /// </summary>
    /// <remarks>
    /// 整个过程要一两秒，全程在后台线程上跑，UI 只负责显示忙碌态。
    /// 检测不到人脸就退回按名单抽，并在卡片上说明原因——静默失败比抽错人更让人摸不着头脑。
    /// </remarks>
    private void PickFromPhoto()
    {
        if (_shooting)
        {
            return;
        }

        _shooting = true;
        _window?.SetBusy(true);

        _ = Task.Run(async () =>
        {
            var result = await CameraPicker.CaptureAndPickAsync(
                _settings, PluginDirectory, _configFolder, count: PickCount);
            Dispatcher.UIThread.Post(() =>
            {
                _shooting = false;
                _window?.SetBusy(false);

                if (result is { Success: true } && result.Portraits.Count > 0)
                {
                    RevealWindow.Show(result.Portraits, null,
                        _settings.PortraitHeight,
                        TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
                        _window?.Accent ?? DefaultAccent);
                    return;
                }

                // 拍不到 / 认不出来就退回名单，但要把原因说清楚。
                PickFromRoster($"拍照失败:{result.Message}，已改为按名单抽取");
            });
        });
    }

    /// <summary>
    /// 文字抽选用的名单。
    /// </summary>
    /// <remarks>
    /// 开了「文字抽选用单独名单」就读那一份，否则还是主名单。
    /// 两份各自有各自的「本轮已抽」状态吗？——没有，共用一套。
    /// 名单隔离是为了圈定范围，不是为了各记各的进度。
    /// </remarks>
    private RosterService TextRoster
    {
        get
        {
            if (!_settings.SeparateTextRoster)
            {
                return _roster!;
            }

            _textRoster ??= new RosterService(
                Path.Combine(_configFolder, "名单-文字.txt"));

            return _textRoster;
        }
    }

    private RosterService? _textRoster;

    /// <summary>按名单抽 N 个。</summary>
    /// <param name="note">附带说明，比如从拍照模式退回来的原因。</param>
    private void PickFromRoster(string? note = null)
    {
        if (_roster is null)
        {
            return;
        }

        var names = TextRoster.PickMany(_settings, PickCount);
        SaveSettingsInternal();
        _window?.RefreshCounter();

        if (names.Count == 0)
        {
            // 名单是空的——与其静悄悄什么都不发生，不如直接把话说清楚。
            Reveal(note is null ? "名单是空的" : note, isHint: true);
            return;
        }

        if (_settings.ShowCenterReveal)
        {
            // 多个人竖着排。横着连成一排的话，三个名字在大字号下会宽到出屏。
            Reveal(string.Join('\n', names), isHint: false, note);
        }

        if (_settings.ShowNotification)
        {
            SendNotification(string.Join("、", names));
        }
    }

    /// <summary>
    /// 一次抽几个。菜单和设置页给的是 1 / 2 / 3，这里再兜一次底，防止配置被人手改成别的值。
    /// </summary>
    private int PickCount => Math.Clamp(_settings.PickCount, 1, 3);

    private void Reveal(string text, bool isHint, string? note = null)
    {
        // 复用已经开着的那个窗口（内部会处理），连点也不会叠出一摞。
        RevealWindow.Show(
            text,
            note,
            isHint ? _settings.RevealFontSize * 0.42 : _settings.RevealFontSize,
            TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds, 0.5, 30)),
            _window?.Accent ?? DefaultAccent);
    }

    private static readonly Avalonia.Media.Color DefaultAccent =
        Avalonia.Media.Color.FromRgb(0x5B, 0x8D, 0xEF);

    private void SendNotification(string name)
    {
        // 提供方是宿主用 AddHostedService 建的，这里按类型把那一份取回来。
        var provider = IAppHost.Host?.Services
            .GetServices<IHostedService>()
            .OfType<PickerNotificationProvider>()
            .FirstOrDefault();
        if (provider is null)
        {
            return;
        }

        // 连着抽人时，上一条还没播完就来了下一条，主界面上会排队堆积。
        // 直接把上一条取消掉——用户只关心最新抽到的那个人。
        _lastRequest?.Cancel();

        var request = new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask("随机抽选", hasRightIcon: false, factory: x =>
            {
                // 遮罩只是个引子，压得短一点，让名字尽快出来；连着抽时也不至于一直卡在遮罩上。
                x.Duration = TimeSpan.FromSeconds(0.9);
                x.IsSpeechEnabled = false;
            }),
            OverlayContent = NotificationContent.CreateSimpleTextContent(name, factory: x =>
            {
                x.Duration = TimeSpan.FromSeconds(Math.Clamp(_settings.RevealSeconds + 2.0, 2.0, 30));
                x.IsSpeechEnabled = false;
            })
        };

        _lastRequest = request;
        provider.ShowNotification(request);
    }
}
