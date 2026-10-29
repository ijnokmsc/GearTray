using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GearTray.Core;

namespace GearTray.Host.Panel;

/// <summary>
/// M1/M4 占位面板：只验证数据链路、DPI 控制与 Win 锁，视觉在 M1.5 换成 Skin + 布局模板。
///
/// Win 锁只对**键盘固件**可用（N75 PRO 已打通：功能信息块第 21 字节）。
/// 宿主不再挂全局键盘钩子去冒充这件事——那只是本机软件行为，退出即失效。
/// </summary>
public partial class PanelWindow : Window
{
    /// <summary>色板预设。不支持自由取色：固件只有一个 RGB 三元组，预设够用且不会写出奇怪的值。</summary>
    private static readonly (string Name, int Rgb)[] ColorPresets =
    [
        ("白", 0xFFFFFF), ("红", 0xFF0000), ("橙", 0xFF8000), ("黄", 0xFFFF00),
        ("绿", 0x00FF00), ("青", 0x00FFFF), ("蓝", 0x0000FF), ("紫", 0x8000FF),
    ];

    /// <summary>编辑控件列宽。三列是 120/150/*，值标签因此每一行都对齐在同一个 x 上。</summary>
    private const double EditorWidth = 150;

    private readonly Action _onRefresh;
    private readonly Func<DeviceIdentity, int, Task<bool>> _onSetStage;
    private readonly Func<DeviceIdentity, bool, Task<bool>> _onSetWinLock;
    private readonly Func<DeviceIdentity, string, int, Task<bool>> _onSetSetting;

    private DeviceIdentity? _dpiTarget;
    private DeviceIdentity? _lockTarget;
    private DeviceIdentity? _settingsTarget;
    private string _lockSignature = string.Empty;
    private bool _busy;

    /// <summary>DPI 按钮只建一次，之后就地更新选中态。</summary>
    private readonly List<Button> _dpiButtons = [];
    private string _dpiStructure = string.Empty;
    private int _dpiActive = -1;

    /// <summary>设置行只建一次，之后就地同步值。重建只在设备/字段表结构变化时发生。</summary>
    private readonly List<SettingRow> _rows = [];
    private string _structureSignature = string.Empty;

    /// <summary>刚写完一次，下一轮必须无条件把设备真值刷回控件（失败时驱动已回滚）。</summary>
    private bool _forceResync;

    private List<DeviceRow> _deviceRows = [];

    /// <summary>重建控件时赋值会触发 SelectionChanged，必须压住，否则一刷新就自己写一遍设备。</summary>
    private bool _suppressSettingEvents;

    public PanelWindow(
        Action onRefresh,
        Func<DeviceIdentity, int, Task<bool>> onSetStage,
        Func<DeviceIdentity, bool, Task<bool>> onSetWinLock,
        Func<DeviceIdentity, string, int, Task<bool>> onSetSetting)
    {
        InitializeComponent();
        _onRefresh = onRefresh;
        _onSetStage = onSetStage;
        _onSetWinLock = onSetWinLock;
        _onSetSetting = onSetSetting;
    }

    public void Apply(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        // 内容没变就不换 ItemsSource：重设会拆掉全部 item 容器，
        // 既闪一下也把滚动位置和焦点一起丢掉。
        var rows = snapshots
            .Select(snapshot => new DeviceRow(
                snapshot.Identity.DisplayName,
                Describe(snapshot),
                snapshot.Battery.Display))
            .ToList();

        if (!rows.SequenceEqual(_deviceRows))
        {
            _deviceRows = rows;
            DeviceList.ItemsSource = rows;
        }

        var summary = snapshots.Count == 0 ? "未发现设备" : $"{snapshots.Count} 个 HID 接口";
        if (SummaryText.Text != summary)
        {
            SummaryText.Text = summary;
        }

        ApplyWinLock(snapshots);
        ApplyDpi(snapshots);
        ApplySettings(snapshots);
    }

    /// <summary>只有实现固件 Win 锁的设备才显示这一行；否则整行收起，不画灰开关。</summary>
    private void ApplyWinLock(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        var target = snapshots.FirstOrDefault(s => s.WinKeyLock is { IsAvailable: true });
        _lockTarget = target?.Identity;

        if (target?.WinKeyLock is not { } state)
        {
            _lockSignature = string.Empty;
            WinLockRow.Visibility = Visibility.Collapsed;
            return;
        }

        WinLockRow.Visibility = Visibility.Visible;

        var signature = $"{target.Identity.DevicePath}|{state.IsLocked}|{state.LastError}";
        if (signature == _lockSignature)
        {
            return;
        }

        _lockSignature = signature;
        WinLockCheck.IsChecked = state.IsLocked;
        WinLockHint.Text = state.IsLocked ? "已锁定：Win 键被固件屏蔽" : "未锁定";
    }

    private async void OnWinLockClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _lockTarget is not { } identity)
        {
            return;
        }

        var wanted = WinLockCheck.IsChecked == true;

        try
        {
            _busy = true;
            WinLockCheck.IsEnabled = false;
            WinLockHint.Text = "写入中…";

            if (!await _onSetWinLock(identity, wanted))
            {
                // 回滚到设备真实状态，绝不留下「点过了但没生效」的假象。
                WinLockCheck.IsChecked = !wanted;
                WinLockHint.Text = "写入失败（键盘可能休眠，按一下键再试）";
            }
            else
            {
                WinLockHint.Text = wanted ? "已锁定：Win 键被固件屏蔽" : "未锁定";
            }
        }
        catch (Exception ex)
        {
            WinLockCheck.IsChecked = !wanted;
            WinLockHint.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            WinLockCheck.IsEnabled = true;
        }
    }

    /// <summary>
    /// DPI 档位按钮**只建一次**，之后就地更新选中态。
    ///
    /// 以前是每轮 Clear + Add。代价不只是闪烁和焦点丢失：在写入进行中把承载焦点的
    /// 控件从可视树摘掉，会踩到 WPF 内部的 <c>DispatcherOperationTaskSource</c> 初始化失败
    /// （CLR 0x80131506，紧接着段错误退出）。所以「设备暂时读不到」这条路径也不清树，只置灰。
    /// </summary>
    private void ApplyDpi(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        var target = snapshots.FirstOrDefault(s => s.Dpi is not null && s.Dpi.Stages.Count > 0);
        _dpiTarget = target?.Identity;

        if (target?.Dpi is not { } dpi)
        {
            if (_dpiButtons.Count > 0)
            {
                DpiPanel.IsEnabled = false;
                DpiTitle.Text = "DPI 档位（设备未连接）";
            }
            else
            {
                DpiTitle.Text = "DPI 档位（本设备未识别 DPI 协议）";
            }

            return;
        }

        // 结构 = 设备 + 档位列表。档位本身不会变，所以这棵树实际上只建一次。
        var structure = $"{target.Identity.DevicePath}|{string.Join(',', dpi.Stages)}";
        if (structure != _dpiStructure && !_busy)
        {
            _dpiStructure = structure;
            _dpiButtons.Clear();
            DpiPanel.Children.Clear();

            for (var i = 0; i < dpi.Stages.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    Content = dpi.Stages[i].ToString(),
                    Padding = new Thickness(12, 4, 12, 4),
                    Margin = new Thickness(0, 0, 6, 6),
                    Tag = index,
                    ToolTip = $"第 {index + 1} 档 · {dpi.Stages[i]} DPI",
                };

                button.Click += OnDpiStageClick;
                _dpiButtons.Add(button);
                DpiPanel.Children.Add(button);
            }

            _dpiActive = -1;
        }

        if (!_busy)
        {
            DpiPanel.IsEnabled = true;
        }

        DpiTitle.Text = $"DPI 档位（当前 {dpi.ActiveDpi}）";

        if (_dpiActive == dpi.ActiveIndex)
        {
            return;
        }

        _dpiActive = dpi.ActiveIndex;
        for (var i = 0; i < _dpiButtons.Count; i++)
        {
            var button = _dpiButtons[i];
            var active = i == dpi.ActiveIndex;

            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            if (active)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xD2));
                button.Foreground = Brushes.White;
            }
            else
            {
                button.ClearValue(Button.BackgroundProperty);
                button.ClearValue(Button.ForegroundProperty);
            }
        }
    }

    private async void OnDpiStageClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: int index } || _dpiTarget is not { } identity)
        {
            return;
        }

        try
        {
            _busy = true;
            DpiPanel.IsEnabled = false;

            if (!await _onSetStage(identity, index))
            {
                MessageBox.Show(this, "写入失败：设备可能已休眠，动一下鼠标再试。", "DPI",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DPI", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            DpiPanel.IsEnabled = true;
        }
    }

    /// <summary>
    /// 键盘设置区：控件**建一次**，之后只就地同步值。
    /// 字段表、选项、标签全部由驱动经快照送上来，面板不认识任何具体设备。
    ///
    /// 这里绝不在轮询路径上 <c>Children.Clear()</c>：设备一休眠就清空、恢复后再重建，
    /// 正好撞在用户点着下拉框的时刻，WPF 会在卸载 ComboBox 的路径上崩掉
    /// （CLR 0x80131506 → 段错误，整只托盘进程消失）。结构变了才重建。
    /// </summary>
    private void ApplySettings(IReadOnlyList<DeviceSnapshot> snapshots)
    {
        var target = snapshots.FirstOrDefault(s => s.Settings is { Count: > 0 });
        _settingsTarget = target?.Identity;

        if (target?.Settings is not { Count: > 0 } settings)
        {
            // 设备休眠/临时读不到：控件全部保留，只置灰，等它回来。
            if (_rows.Count > 0)
            {
                SettingsPanel.IsEnabled = false;
                SettingsStatus.Text = "设备未连接";
            }

            // 没有可调项但有固件 Win 锁的设备，这一块仍要显示（里面只有 Win 锁一行）。
            SettingsTitle.Text = "键盘设置";
            SettingsRow.Visibility = snapshots.Any(s => s.WinKeyLock is { IsAvailable: true })
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        SettingsRow.Visibility = Visibility.Visible;
        SettingsTitle.Text = $"键盘设置（{target.Identity.DisplayName}）";

        // 结构 = 设备 + 字段表（键 / 分组 / 标签 / 控件类型 / 选项）。只有它变了才重建。
        var structure = $"{target.Identity.DevicePath}|" + string.Join(';', settings.Select(s =>
            $"{s.Key}:{s.Group}:{s.Label}:{(int)s.Kind}:" +
            string.Join(',', s.Options.Select(o => $"{o.Value}={o.Label}"))));

        if (structure != _structureSignature && !_busy)
        {
            _structureSignature = structure;
            _forceResync = true;
            RebuildSettings(settings);
        }

        SyncSettings(settings);
    }

    private void RebuildSettings(IReadOnlyList<DeviceSetting> settings)
    {
        _suppressSettingEvents = true;
        try
        {
            SettingsPanel.Children.Clear();
            _rows.Clear();

            foreach (var group in settings.GroupBy(s => s.Group))
            {
                SettingsPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 8, 0, 2),
                });

                foreach (var setting in group)
                {
                    var row = BuildSettingRow(setting);
                    _rows.Add(row);
                    SettingsPanel.Children.Add(row.Element);
                }
            }
        }
        finally
        {
            _suppressSettingEvents = false;
        }
    }

    private void SyncSettings(IReadOnlyList<DeviceSetting> settings)
    {
        var byKey = settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

        _suppressSettingEvents = true;
        try
        {
            foreach (var row in _rows)
            {
                if (!byKey.TryGetValue(row.Key, out var setting))
                {
                    continue;
                }

                if (!_forceResync && setting.Value == row.AppliedValue)
                {
                    continue;
                }

                // 用户正把光标放在这一行时别抢。刚写完需要刷回真值的那一次例外。
                if (!_forceResync &&
                    (row.Editor.IsKeyboardFocusWithin || row.Editor is ComboBox { IsDropDownOpen: true }))
                {
                    continue;
                }

                row.Sync(setting);
                row.ValueText.Text = setting.DisplayValue;
                row.AppliedValue = setting.Value;
            }
        }
        finally
        {
            _suppressSettingEvents = false;
            _forceResync = false;
        }

        if (!_busy)
        {
            SettingsPanel.IsEnabled = true;
        }
    }

    private SettingRow BuildSettingRow(DeviceSetting setting)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = setting.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = setting.Hint,
        };
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = setting.DisplayValue,
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var (editor, sync) = setting.Kind switch
        {
            DeviceSettingKind.Toggle => BuildToggle(setting),
            DeviceSettingKind.Color => BuildColorPicker(setting),
            _ => BuildChoice(setting),
        };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        return new SettingRow
        {
            Key = setting.Key,
            Element = grid,
            Editor = editor,
            ValueText = value,
            Sync = sync,
            AppliedValue = setting.Value,
        };
    }

    private (FrameworkElement Editor, Action<DeviceSetting> Sync) BuildToggle(DeviceSetting setting)
    {
        // Options[0] 恒为「开」。0=开的固件位（灯效开关、六键无冲）在字段表里已经把 0 放在第一项。
        var on = setting.Options.Count > 0 ? setting.Options[0].Value : 1;
        var off = setting.Options.Count > 1 ? setting.Options[1].Value : 0;

        var check = new CheckBox
        {
            IsChecked = setting.IsOn,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = setting.Hint,
        };

        check.Click += async (_, _) =>
        {
            if (_suppressSettingEvents || check.IsChecked is not { } wanted)
            {
                return;
            }

            await CommitSettingAsync(setting.Key, wanted ? on : off);
        };

        void Sync(DeviceSetting current)
        {
            if (check.IsChecked != current.IsOn)
            {
                check.IsChecked = current.IsOn;
            }
        }

        return (new Grid { Width = EditorWidth, Children = { check } }, Sync);
    }

    private (FrameworkElement Editor, Action<DeviceSetting> Sync) BuildChoice(DeviceSetting setting)
    {
        var combo = new ComboBox
        {
            ItemsSource = setting.Options,
            DisplayMemberPath = nameof(DeviceSettingOption.Label),
            SelectedValuePath = nameof(DeviceSettingOption.Value),
            SelectedValue = setting.Value,
            Width = EditorWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = setting.Hint,
        };

        combo.SelectionChanged += async (_, _) =>
        {
            if (_suppressSettingEvents || combo.SelectedValue is not int picked || picked == setting.Value)
            {
                return;
            }

            await CommitSettingAsync(setting.Key, picked);
        };

        void Sync(DeviceSetting current)
        {
            if (combo.SelectedValue is int shown && shown == current.Value)
            {
                return;
            }

            combo.SelectedValue = current.Value;
        }

        return (combo, Sync);
    }

    private (FrameworkElement Editor, Action<DeviceSetting> Sync) BuildColorPicker(DeviceSetting setting)
    {
        var panel = new WrapPanel
        {
            Width = EditorWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var swatches = new List<(int Rgb, Button Button)>();

        foreach (var (name, rgb) in ColorPresets)
        {
            var selected = rgb == setting.Value;
            var swatch = new Button
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(
                    (byte)((rgb >> 16) & 0xFF),
                    (byte)((rgb >> 8) & 0xFF),
                    (byte)(rgb & 0xFF))),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected ? Brushes.Black : Brushes.LightGray,
                ToolTip = name,
            };

            var value = rgb;
            swatch.Click += async (_, _) => await CommitSettingAsync(setting.Key, value);
            swatches.Add((rgb, swatch));
            panel.Children.Add(swatch);
        }

        void Sync(DeviceSetting current)
        {
            foreach (var (rgb, button) in swatches)
            {
                var selected = rgb == current.Value;
                button.BorderThickness = new Thickness(selected ? 2 : 1);
                button.BorderBrush = selected ? Brushes.Black : Brushes.LightGray;
            }
        }

        return (panel, Sync);
    }

    private async Task CommitSettingAsync(string key, int value)
    {
        if (_busy || _settingsTarget is not { } identity)
        {
            return;
        }

        try
        {
            _busy = true;
            SettingsPanel.IsEnabled = false;
            SettingsStatus.Text = "写入中…";

            var ok = await _onSetSetting(identity, key, value);

            SettingsStatus.Text = ok
                ? "已写入（回读确认）"
                : "写入失败：键盘可能休眠，或该型号不支持此项";

            // 无论成败都按设备真值刷一次控件：失败时驱动已整块回滚，
            // 界面必须跟着回去，不能停在用户刚点的那个值上。
            _forceResync = true;
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = ex.Message;
            _forceResync = true;
        }
        finally
        {
            _busy = false;
            SettingsPanel.IsEnabled = true;
            _onRefresh();
        }
    }

    private static string Describe(DeviceSnapshot snapshot)
    {
        var identity = snapshot.Identity;
        var page = $"page 0x{identity.UsagePage:X4}";
        var health = snapshot.Health switch
        {
            DeviceHealth.Online => "已连接",
            DeviceHealth.Asleep => "休眠",
            DeviceHealth.Stale => "数据过期",
            DeviceHealth.Faulted => "读取异常",
            DeviceHealth.Disconnected => "已断开",
            _ => snapshot.Health.ToString(),
        };

        var battery = snapshot.Battery switch
        {
            { Percent: null } => "未识别电量协议",
            { IsStale: true } => "电量沿用旧值",
            _ => $"采样于 {snapshot.Battery.SampledAt:HH:mm:ss}",
        };

        return $"{identity.VidPid} · {page} · {health} · {battery}";
    }

    /// <summary>
    /// 把当前面板渲染成 PNG。用于开发期核对布局（截图工具要么被策略挡、要么要抓屏幕），
    /// 走的是 WPF 自己的渲染管线，窗口不在最前也能截到。
    /// </summary>
    public void SaveSnapshot(string path)
    {
        UpdateLayout();

        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _onRefresh();

    private void OnExitClick(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

    private sealed record DeviceRow(string Name, string Detail, string BatteryText);

    /// <summary>
    /// 一行设置：控件 + 值标签 + 就地同步动作。
    /// 之所以不每轮重建，是因为重建要在设备交互中途拆掉可视树，会崩（见 ApplySettings）。
    /// </summary>
    private sealed class SettingRow
    {
        public required string Key { get; init; }

        public required FrameworkElement Element { get; init; }

        /// <summary>真正承载交互的控件。用来判断用户是否正停在这一行上。</summary>
        public required FrameworkElement Editor { get; init; }

        public required TextBlock ValueText { get; init; }

        public required Action<DeviceSetting> Sync { get; init; }

        /// <summary>上一次同步到界面上的设备值，用来跳过没变化的行。</summary>
        public int AppliedValue { get; set; }
    }
}
