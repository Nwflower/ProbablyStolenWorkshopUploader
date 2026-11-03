using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.Graphics;
using WinRT.Interop;

namespace ProbablyStolenWorkshopUploader
{
    public sealed partial class MainWindow : Window
    {
        private static readonly string[] LanguageKeys =
        {
            "english", "schinese", "russian", "spanish", "french", "german", "polish", "japanese"
        };

        private static readonly string[] LanguageLabels =
        {
            "English", "简体中文", "Русский", "Español", "Français", "Deutsch", "Polski", "日本語"
        };

        /// <summary>Steam 允许单个工坊物品设置的 TAG 数量上限。</summary>
        private const int MaxTags = 20;

        /// <summary>常见工坊 TAG，给出快捷添加按钮；仍可自由输入其它标签。</summary>
        private static readonly string[] CommonTags =
        {
            "Mod", "Gameplay", "Mono", "Translation", "Tools", "Quality of Life",
            "Utilities", "UI", "Chinese", "Music", "AI", "Assistant", "Streaming"
        };

        private readonly SteamUploader _steam = new SteamUploader();
        private readonly MetaStore _meta = new MetaStore(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PSMOD_UPLOADER_META_DIR"))
                ? null : Environment.GetEnvironmentVariable("PSMOD_UPLOADER_META_DIR"));
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly ObservableCollection<ModRow> _rows = new ObservableCollection<ModRow>();

        private readonly ObservableCollection<PublishTarget> _targets = new ObservableCollection<PublishTarget>();
        private readonly LangBuf[] _langs = new LangBuf[LanguageKeys.Length];

        private List<ModRow> _allRows = new List<ModRow>();
        private ModRow _current;
        private string _previewPath = "";
        private string _gameDir = "";
        private int _langIndex;
        private bool _loading;
        private bool _suppressSelection;
        private bool _uploading;
        private bool _queriedOnce;
        private bool _targetSelectionDirty;
        private string _publishingModId = "";
        private bool _publishingWasNew;
        private bool _previewPointerOver;
        private bool _communityNoticeShown;
        private string _tagHintContext = "";
        private bool? _compactLayout;
        private bool? _stackPreview;
        private double _minimumWindowScale;
        private DateTime _loginDeadline = DateTime.MinValue;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public MainWindow()
        {
            InitializeComponent();
            try
            {
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    ExtendsContentIntoTitleBar = true;
                    SetTitleBar(AppTitleBar);
                }
                else AppTitleBar.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ExtendsContentIntoTitleBar = false;
                AppTitleBar.Visibility = Visibility.Collapsed;
            }
            BuildLanguages();
            BuildTagSuggestions();

            ModList.ItemsSource = _rows;
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _steam.Log += AppendLog;
            _steam.Failed += msg => AppendLog("✘ " + msg);
            _steam.Published += OnPublished;
            _steam.Completed += OnPublishCompleted;
            _steam.Unconfirmed += OnPublishUnconfirmed;
            _steam.QueryCompleted += OnQueryCompleted;

            Root.Loaded += MainWindow_Loaded;
            Root.SizeChanged += Root_SizeChanged;
            HeroGrid.SizeChanged += HeroGrid_SizeChanged;
            Closed += (_, __) => { SaveCurrent(); _timer.Stop(); _steam.Shutdown(); };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "probably-stolen.ico");
                if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { }

            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                uint dpi = GetDpiForWindow(hwnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                int width = (int)(1180 * scale);
                int height = (int)(820 * scale);
                DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                if (area != null)
                {
                    width = Math.Min(width, area.WorkArea.Width);
                    height = Math.Min(height, area.WorkArea.Height);
                }
                AppWindow.Resize(new SizeInt32(width, height));
            }
            catch { }

            DiscoverMods();
            InitSteam();
            _ = LoadPreviewChangeIconAsync();
            UpdateMinimumWindowSize();
            Root.XamlRoot.Changed += (_, __) => UpdateMinimumWindowSize();
            UpdateLayoutForWidth(Root.ActualWidth);
            UpdatePublishAvailability();
            await ShowFirstCommunityNoticeAsync();
        }

        private void UpdateMinimumWindowSize()
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            if (Math.Abs(_minimumWindowScale - scale) < 0.001) return;
            if (!(AppWindow.Presenter is OverlappedPresenter presenter)) return;

            try
            {
                int width = (int)Math.Ceiling(640 * scale);
                int height = (int)Math.Ceiling(640 * scale);
                DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                if (area != null)
                {
                    if (area.WorkArea.Width > 0) width = Math.Min(width, area.WorkArea.Width);
                    if (area.WorkArea.Height > 0) height = Math.Min(height, area.WorkArea.Height);
                }
                presenter.PreferredMinimumWidth = width;
                presenter.PreferredMinimumHeight = height;
                _minimumWindowScale = scale;
            }
            catch { /* 不支持最小尺寸时保留可缩放布局。 */ }
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayoutForWidth(e.NewSize.Width);
            if (LogPanel.Visibility == Visibility.Visible) UpdateLogHeight();
        }

        private void UpdateLayoutForWidth(double width)
        {
            if (width <= 0) return;
            bool compact = width < 760;
            if (_compactLayout != compact)
            {
                _compactLayout = compact;
                Root.Padding = compact ? new Thickness(12) : new Thickness(24, 12, 24, 20);
                WorkspaceGrid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 1020 ? 240 : 300);
                WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 14);
                WorkspaceGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                WorkspaceGrid.RowDefinitions[0].Height = compact ? new GridLength(190) : GridLength.Auto;
                WorkspaceGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
                Grid.SetRow(SidebarCard, 0);
                Grid.SetRowSpan(SidebarCard, compact ? 1 : 2);
                Grid.SetColumn(SidebarCard, 0);
                Grid.SetColumnSpan(SidebarCard, compact ? 3 : 1);
                Grid.SetRow(MainPane, compact ? 1 : 0);
                Grid.SetRowSpan(MainPane, compact ? 1 : 2);
                Grid.SetColumn(MainPane, compact ? 0 : 2);
                Grid.SetColumnSpan(MainPane, compact ? 3 : 1);
                MainPane.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
                SidebarLayout.RowSpacing = compact ? 6 : 10;
                SidebarLayout.RowDefinitions[2].Height = compact ? new GridLength(0) : GridLength.Auto;
                Grid.SetColumnSpan(SearchBox, compact ? 1 : 2);
                Grid.SetRow(SidebarActions, compact ? 1 : 2);
                Grid.SetColumn(SidebarActions, compact ? 1 : 0);
                Grid.SetColumnSpan(SidebarActions, compact ? 1 : 2);
            }

            if (compact)
            {
                double sidebarHeight = Math.Max(160, Math.Min(210, Root.ActualHeight * 0.28));
                WorkspaceGrid.RowDefinitions[0].Height = new GridLength(sidebarHeight);
            }
            else
                WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(width < 1020 ? 240 : 300);
        }

        private void HeroGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0) return;
            bool stackPreview = e.NewSize.Width < 390;
            if (_stackPreview != stackPreview)
            {
                _stackPreview = stackPreview;
                Grid.SetRow(PreviewPanel, stackPreview ? 1 : 0);
                Grid.SetColumn(PreviewPanel, stackPreview ? 0 : 1);
                Grid.SetColumnSpan(PreviewPanel, stackPreview ? 2 : 1);
                PreviewPanel.Margin = stackPreview ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
            }
        }

        // ───────────────────────── Steam ─────────────────────────

        private void InitSteam()
        {
            AppendLog("正在初始化 Steam…");
            SetSteamState("正在初始化 Steam…", Windows.UI.Color.FromArgb(255, 157, 93, 0));

            if (!_steam.Init(SteamUploader.DefaultAppId))
            {
                SetSteamState("Steam 初始化失败", Windows.UI.Color.FromArgb(255, 196, 43, 28));
                return;
            }

            _loginDeadline = DateTime.UtcNow.AddSeconds(8);
            SetSteamState("等待 Steam 登录…", Windows.UI.Color.FromArgb(255, 157, 93, 0));
        }

        private void RetryBtn_Click(object sender, RoutedEventArgs e)
        {
            _steam.Shutdown();
            _queriedOnce = false;
            InitSteam();
        }

        private void SetSteamState(string text, Windows.UI.Color color)
        {
            SteamStatusText.Text = text;
            StatusDot.Fill = new SolidColorBrush(color);
            ToolTipService.SetToolTip(SteamStatusText, text);
            UpdatePublishAvailability();
        }

        private void Timer_Tick(object sender, object e)
        {
            _steam.Pump();

            if (_loginDeadline != DateTime.MinValue)
            {
                if (_steam.IsLoggedOn())
                {
                    SetSteamState("Steam 已连接", Windows.UI.Color.FromArgb(255, 15, 123, 15));
                    _loginDeadline = DateTime.MinValue;
                    AppendLog("Steam 已登录并连接。");

                    if (!_queriedOnce)
                    {
                        _queriedOnce = true;
                        RefreshTargets();
                    }
                }
                else if (DateTime.UtcNow >= _loginDeadline)
                {
                    _loginDeadline = DateTime.MinValue;
                    SetSteamState("Steam 未连接", Windows.UI.Color.FromArgb(255, 196, 43, 28));
                    AppendLog("✘ Steam 客户端未连上服务器。常见原因：之前用 steamcmd 登录把客户端顶下线，需完全退出并重启 Steam。");
                }
            }

            if (_loginDeadline == DateTime.MinValue && _steam.Ready)
            {
                bool online = _steam.IsLoggedOn();
                if (!online && _queriedOnce)
                {
                    _queriedOnce = false;
                    SetSteamState("Steam 未连接", Windows.UI.Color.FromArgb(255, 196, 43, 28));
                    AppendLog("✘ Steam 已断开连接；请确认 Steam 在线后再发布。");
                }
                else if (online && !_queriedOnce)
                {
                    _queriedOnce = true;
                    SetSteamState("Steam 已连接", Windows.UI.Color.FromArgb(255, 15, 123, 15));
                    AppendLog("Steam 已连接。");
                    RefreshTargets();
                }
            }

            if (_uploading)
            {
                double p = _steam.GetProgress(out string st, out ulong done, out ulong total);
                if (p > 0) UploadProgress.Value = Math.Round(p * 100);
                if (!string.IsNullOrEmpty(st))
                    StatusText.Text = total > 0 ? $"{st}  {done / 1024}/{total / 1024} KB" : st;
            }
        }

        // ───────────────────────── 嗅探 ─────────────────────────

        private void DiscoverMods()
        {
            _gameDir = ModDiscovery.FindGameDir();
            GameDirText.Text = string.IsNullOrEmpty(_gameDir)
                ? "Probably Stolen · " + ModDiscovery.LastGameDirError
                : "游戏目录 · " + _gameDir;
            ToolTipService.SetToolTip(GameDirText, GameDirText.Text);

            if (!string.IsNullOrEmpty(_gameDir) && InfoPath.Text.Length == 0)
                AppendLog("游戏目录：" + _gameDir);

            List<DiscoveredMod> found = ModDiscovery.Scan(_gameDir); // 只列本机 Mods/
            _allRows = found.Select(m => new ModRow(m)).ToList();
            AppendLog($"扫描到 {found.Count} 个本地模组。");

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = (SearchBox.Text ?? "").Trim();

            _suppressSelection = true;
            _rows.Clear();
            foreach (ModRow row in _allRows)
            {
                if (q.Length == 0 ||
                    row.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    row.Mod.ModId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _rows.Add(row);
                }
            }
            _suppressSelection = false;

            if (ModCountText != null)
                ModCountText.Text = q.Length == 0 ? $"{_allRows.Count} 个" : $"{_rows.Count} / {_allRows.Count}";
            if (EmptyListHint != null)
            {
                EmptyListHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyListHint.Text = q.Length == 0 ? "没有找到模组\n可以选择文件夹手动加入" : "没有匹配的模组\n试试其他名称或 ID";
            }

            if (_current != null && _rows.Contains(_current))
                ModList.SelectedItem = _current;
            else if (_rows.Count > 0 && _current == null)
                ModList.SelectedIndex = 0;
        }

        private void RescanBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrent();
            DiscoverMods();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            string folder = await PickFolderAsync("选择包含 manifest.xml 的 mod 文件夹");
            if (folder == null) return;

            ManifestLoadResult result = ModManifestInfo.Validate(folder);
            if (result.HasErrors)
            {
                await ShowManifestIssuesAsync(result);
                return;
            }

            AddModFolder(folder, result);
        }

        private void AddModFolder(string folder, ManifestLoadResult result)
        {
            ModManifestInfo info = result.Manifest;
            var mod = new DiscoveredMod
            {
                Folder = folder,
                Source = ModSource.Local,
                Manifest = info,
                ManifestResult = result,
                PreviewPath = ModDiscovery.FindPreview(folder),
            };
            var row = new ModRow(mod);

            ModRow existing = _allRows.FirstOrDefault(r => r.Mod.ModId == mod.ModId);
            if (existing != null) _allRows.Remove(existing);
            _allRows.Insert(0, row);

            SearchBox.Text = "";
            ApplyFilter();
            ModList.SelectedItem = row;
            if (_current != row)
            {
                SaveCurrent();
                LoadMod(row);
            }
            AppendLog($"已加入 {info.Name}（{info.Id} v{info.Version}）。");
            if (result.Warnings.Any())
                AppendLog("⚠ 清单有 " + result.Warnings.Count() + " 个警告：" +
                          string.Join("；", result.Warnings.Select(w => w.Summary)));
        }

        private async Task<string> PickFolderAsync(string description)
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFolder folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }

        // ───────────────────────── 选择与编辑 ─────────────────────────

        private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection) return;
            if (!(ModList.SelectedItem is ModRow row)) return;

            SaveCurrent();
            LoadMod(row);
        }

        private void LoadMod(ModRow row)
        {
            _loading = true;
            _current = row;
            _targetSelectionDirty = false;
            KeepDescriptionCheck.IsChecked = true;

            ModManifestInfo manifest = row.Mod.Manifest;
            InfoName.Text = row.Mod.IsBroken ? row.Mod.Name + "（清单无效）" : manifest.Name;
            InfoMeta.Text = row.Mod.IsBroken ? DescribeBrokenManifest(row.Mod) : DescribeValidManifest(row.Mod);
            if (row.Mod.IsBroken)
                InfoMeta.Foreground = BrushResource("SystemFillColorCriticalBrush", Microsoft.UI.Colors.Firebrick);
            else
                InfoMeta.ClearValue(TextBlock.ForegroundProperty);
            ToolTipService.SetToolTip(InfoMeta,
                row.Mod.ManifestResult != null && row.Mod.ManifestResult.Warnings.Any()
                    ? string.Join("\n", row.Mod.ManifestResult.Warnings.Select(w => "⚠ " + w.Summary))
                    : null);

            if (ManifestIssuesBtn != null)
            {
                int issueCount = row.Mod.ManifestResult != null ? row.Mod.ManifestResult.Issues.Count : 0;
                int errorCount = row.Mod.ManifestResult != null ? row.Mod.ManifestResult.Errors.Count() : 0;
                ManifestIssuesBtn.Visibility = issueCount == 0 ? Visibility.Collapsed : Visibility.Visible;
                ManifestIssuesBtn.Content = errorCount > 0
                    ? $"查看清单问题（{errorCount} 个错误）"
                    : $"查看清单警告（{issueCount} 个）";
            }
            InfoPath.Text = row.Mod.Folder;
            ToolTipService.SetToolTip(InfoPath, row.Mod.Folder);

            ModMeta meta = _meta.Get(row.Mod.IsBroken ? row.Mod.Folder : manifest.Id);

            _previewPath = string.IsNullOrEmpty(meta.PreviewPath) ? row.Mod.PreviewPath : meta.PreviewPath;
            if (!File.Exists(_previewPath)) _previewPath = row.Mod.PreviewPath;

            ImageSource preview = SafeImage(_previewPath);
            PreviewImage.Source = preview;
            PreviewHint.Visibility = preview == null ? Visibility.Visible : Visibility.Collapsed;

            EnsureTargets();
            ulong matched = MatchPublished(manifest, meta);
            SelectTarget(matched);

            ChangeNoteBox.Text = manifest != null ? manifest.Version : "";

            for (int i = 0; i < LanguageKeys.Length; i++)
            {
                string key = LanguageKeys[i];
                LangText t = meta.Languages != null && meta.Languages.TryGetValue(key, out LangText found) ? found : null;
                string title = t != null ? t.Title : "";
                string desc = t != null ? t.Desc : "";

                if (key == "english" && string.IsNullOrEmpty(title) && string.IsNullOrEmpty(desc) && manifest != null)
                {
                    title = manifest.Name;
                    desc = manifest.Description;
                }

                _langs[i] = new LangBuf { Title = title, Desc = desc };
            }

            _langIndex = 0;
            if (LangBox.SelectedIndex != 0) LangBox.SelectedIndex = 0;
            ShowLang(0);

            _loading = false;
            UpdatePublishAvailability();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => DetailScroll.ChangeView(null, 0, null, true));
        }

        /// <summary>本地记忆 → ModId 标签 → 标题，三级匹配已发布的工坊物品。</summary>
        private ulong MatchPublished(ModManifestInfo manifest, ModMeta meta)
        {
            if (meta.PublishedId != 0) return meta.PublishedId;
            if (manifest == null) return 0;

            foreach (PublishTarget item in _targets)
            {
                if (item.Id == 0) continue;
                if (!string.IsNullOrEmpty(item.ModId) &&
                    string.Equals(item.ModId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                    return item.Id;
            }

            foreach (PublishTarget item in _targets)
            {
                if (item.Id == 0) continue;
                if (string.Equals((item.Title ?? "").Trim(), manifest.Name.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    return item.Id;
            }

            return 0;
        }

        private void SaveCurrent()
        {
            if (_loading || _current == null || _current.Mod.IsBroken) return;

            FlushLang();

            ModMeta meta = _meta.Get(_current.Mod.ModId);
            ulong targetId = SelectedTargetId();
            if (meta.PublishedId != targetId) meta.PendingFirstPublish = false;
            meta.PublishedId = targetId;
            if (SelectedVisibility() is int visibility) meta.Visibility = visibility;
            meta.PreviewPath = _previewPath ?? "";
            meta.Tags = ParseTags(TagsBox.Text);

            for (int i = 0; i < LanguageKeys.Length; i++)
            {
                string title = _langs[i]?.Title ?? "";
                string desc = _langs[i]?.Desc ?? "";
                if (title.Length == 0 && desc.Length == 0)
                {
                    meta.Languages.Remove(LanguageKeys[i]);
                    continue;
                }
                LangText t = meta.For(LanguageKeys[i]);
                t.Title = title;
                t.Desc = desc;
            }

            if (!_meta.Save())
            {
                AppendLog("✘ " + _meta.LastError);
                ShowLog();
            }
        }

        private void OnPublished(ulong id)
        {
            AppendLog("物品 ID：" + id);
            if (id == 0 || string.IsNullOrEmpty(_publishingModId)) return;

            if (!_meta.RememberPublishedId(_publishingModId, id, _publishingWasNew ? true : (bool?)null))
            {
                AppendLog("✘ " + _meta.LastError + "；请记下上面的物品 ID，重试时手动输入。");
                ShowLog();
            }
            if (_current?.Mod.ModId == _publishingModId)
            {
                _targetSelectionDirty = true;
                SelectTarget(id);
            }
        }

        // ───────────────────────── 发布目标下拉 ─────────────────────────

        private void RefreshPublishedBtn_Click(object sender, RoutedEventArgs e) => RefreshTargets();

        private void RefreshTargets()
        {
            if (!_steam.Ready) return;
            StatusText.Text = "查询已发布物品…";
            _steam.QueryMyItems();
        }

        private void OnQueryCompleted(List<WorkshopItemInfo> items)
        {
            if (items == null)
            {
                StatusText.Text = "查询失败，请重试或手动输入物品 ID";
                ShowLog();
                return;
            }
            // 发布中的选择和可见性由本次发布固定；完成后会重新查询。
            if (_uploading) return;

            ulong previousId = SelectedTargetId();
            int? previousVisibility = SelectedVisibility();
            bool? previousKeepDescription = KeepDescriptionCheck.IsChecked;
            string previousTagsText = TagsBox.Text ?? "";
            bool previousKeepTags = KeepTagsCheck.IsChecked == true;
            bool keepSelection = _targetSelectionDirty;
            StatusText.Text = "就绪";
            var targets = new List<PublishTarget> { new PublishTarget { Id = 0 } };

            foreach (WorkshopItemInfo item in items)
            {
                targets.Add(new PublishTarget
                {
                    Id = item.Id,
                    Title = item.Title,
                    ModId = item.ModId,
                    Tags = item.Tags ?? new List<string>(),
                    Visibility = (int)item.Visibility,
                });
            }

            bool wasLoading = _loading;
            _loading = true;
            _targets.Clear();
            foreach (PublishTarget target in targets) _targets.Add(target);
            if (PublishedBox.ItemsSource == null) PublishedBox.ItemsSource = _targets;
            _loading = wasLoading;

            AppendLog(items.Count == 0
                ? "没有查询到已发布的工坊物品。"
                : $"查询到 {items.Count} 个已发布物品。");

            if (_current != null)
            {
                ulong matched = keepSelection ? previousId : MatchPublished(_current.Mod.Manifest, _meta.Get(_current.Mod.ModId));
                SelectTarget(matched);
                if (keepSelection)
                {
                    ApplyVisibility(previousVisibility);
                    ApplyKeepDescription(previousKeepDescription);
                    ApplyKeepTags(previousKeepTags);
                    bool wasTagLoading = _loading;
                    _loading = true;
                    TagsBox.Text = previousTagsText;
                    _loading = wasTagLoading;
                    UpdateTagHint();
                }
            }
        }

        private void EnsureTargets()
        {
            if (PublishedBox.ItemsSource == null)
            {
                _targets.Clear();
                _targets.Add(new PublishTarget { Id = 0 });
                PublishedBox.ItemsSource = _targets;
            }
        }

        private void SelectTarget(ulong id)
        {
            PublishTarget target = _targets.FirstOrDefault(t => t.Id == id);

            bool wasLoading = _loading;
            _loading = true;
            if (target == null && id != 0)
            {
                // 记忆或手动输入的物品不在查询结果中，仍允许指定，但不猜测可见性。
                target = new PublishTarget { Id = id, Title = "手动指定或记忆中的物品" };
                _targets.Add(target);
            }

            PublishedBox.SelectedItem = target ?? _targets.FirstOrDefault();
            _loading = wasLoading;
            SetTargetDefaults(PublishedBox.SelectedItem as PublishTarget);
        }

        private void PublishedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _targetSelectionDirty = true;
            SetTargetDefaults(PublishedBox.SelectedItem as PublishTarget);
        }

        private async void UsePublishedIdBtn_Click(object sender, RoutedEventArgs e)
        {
            ulong currentId = SelectedTargetId();
            var idInput = new TextBox
            {
                PlaceholderText = "输入数字物品 ID",
                Text = currentId == 0 ? "" : currentId.ToString(),
                MaxLength = 20,
            };
            var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = "输入要更新的工坊物品 ID。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(idInput);
            content.Children.Add(error);

            ulong id = 0;
            var dialog = new ContentDialog
            {
                Title = "输入物品 ID",
                Content = content,
                PrimaryButtonText = "使用 ID",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };
            dialog.Opened += (_, __) => idInput.Focus(FocusState.Programmatic);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (ulong.TryParse((idInput.Text ?? "").Trim(), out id) && id != 0) return;
                error.Text = "请输入有效的已有物品 ID；新建物品请在发布目标中选择「新建物品」。";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            EnsureTargets();
            _targetSelectionDirty = true;
            SelectTarget(id);
            SaveCurrent();
        }

        private async void ImportVdfBtn_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".vdf");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                WorkshopVdf vdf = WorkshopVdf.Parse(File.ReadAllText(file.Path));
                if (vdf.AppId != SteamUploader.DefaultAppId)
                    throw new InvalidDataException("VDF 的 appid 不是 Probably Stolen（4348910）。");

                string baseDir = Path.GetDirectoryName(file.Path);
                string folder = Path.GetFullPath(Path.Combine(baseDir, vdf.ContentFolder));
                ManifestLoadResult result = ModManifestInfo.Validate(folder);
                if (result.HasErrors)
                {
                    await ShowManifestIssuesAsync(result);
                    return;
                }

                string preview = null;
                if (vdf.PreviewFile != null)
                {
                    preview = string.IsNullOrWhiteSpace(vdf.PreviewFile)
                        ? "" : Path.GetFullPath(Path.Combine(baseDir, vdf.PreviewFile));
                    if (preview.Length > 0 && !File.Exists(preview))
                        throw new FileNotFoundException("VDF 指定的预览图不存在。", preview);
                }

                AddModFolder(folder, result);
                EnsureTargets();
                _targetSelectionDirty = true;
                SelectTarget(vdf.PublishedFileId);
                if (vdf.Visibility.HasValue) ApplyVisibility(vdf.Visibility);
                if (preview != null)
                {
                    _previewPath = preview;
                    ImageSource image = SafeImage(preview);
                    PreviewImage.Source = image;
                    PreviewHint.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
                }
                if (vdf.ChangeNote != null) ChangeNoteBox.Text = vdf.ChangeNote;

                if (vdf.Tags.Count > 0)
                {
                    bool wasLoading = _loading;
                    _loading = true;
                    TagsBox.Text = string.Join(", ", vdf.Tags);
                    _loading = wasLoading;
                    ApplyKeepTags(false);
                    _tagHintContext = "已从 VDF 导入 TAG；发布前请核对。";
                    UpdateTagHint();
                }

                FlushLang();
                if (vdf.Title != null) _langs[0].Title = vdf.Title;
                if (vdf.Description != null) _langs[0].Desc = vdf.Description;
                if (_langIndex == 0) ShowLang(0);
                SaveCurrent();

                StatusText.Text = "已导入 VDF；请核对发布目标和内容";
                AppendLog("已导入 VDF：" + file.Path + "。已有物品默认保留工坊描述。");
                if (vdf.UnsupportedFields.Count > 0)
                    await WarnAsync("VDF 已导入；以下字段不受此工具支持，未导入：" + string.Join("、", vdf.UnsupportedFields));
            }
            catch (Exception ex)
            {
                await WarnAsync("导入 VDF 失败：" + ex.Message);
            }
        }

        private void SetTargetDefaults(PublishTarget target)
        {
            ulong id = target?.Id ?? 0;
            ApplyVisibility(id == 0 ? 2 : null);
            ModMeta meta = _current != null && !_current.Mod.IsBroken ? _meta.Get(_current.Mod.ModId) : null;
            bool pending = id != 0 && meta != null && meta.PublishedId == id && meta.PendingFirstPublish;
            ApplyKeepDescription(!pending);
            VisibilityHint.Text = id == 0
                ? "新建物品默认隐藏，可在发布前修改。"
                : target.Visibility >= 0
                    ? "查询到的工坊可见性：" + VisibilityLabel(target.Visibility) + "；默认不修改。"
                    : "未查询到工坊可见性；默认不修改。";
            if (pending) VisibilityHint.Text += " 首次发布未完成，请核对描述设置后重试。";
            TargetBadgeText.Text = _current == null ? "等待选择" : _current.Mod.IsBroken
                ? "清单无效" : id == 0 ? "新建物品" : "更新 #" + id;
            ApplyTagDefaults(target);
            UpdatePublishAvailability();
        }

        private void ApplyVisibility(int? v)
        {
            bool wasLoading = _loading;
            _loading = true;
            VisibilityBox.SelectedIndex = v switch { 0 => 1, 1 => 2, 3 => 3, 2 => 4, _ => 0 };
            _loading = wasLoading;
        }

        private void ApplyKeepDescription(bool? value)
        {
            bool wasLoading = _loading;
            _loading = true;
            KeepDescriptionCheck.IsChecked = value;
            _loading = wasLoading;
        }

        // ───────────────────────── 工坊 TAG ─────────────────────────

        private void BuildTagSuggestions()
        {
            TagSuggestionPanel.Children.Clear();
            foreach (string tag in CommonTags)
            {
                var button = new Button
                {
                    Content = tag,
                    FontSize = 12,
                    Padding = new Thickness(10, 4, 10, 4),
                };
                button.Click += (_, __) => AddTagFromSuggestion(tag);
                TagSuggestionPanel.Children.Add(button);
            }
        }

        private void AddTagFromSuggestion(string tag)
        {
            if (_uploading) return;
            List<string> tags = ParseTags(TagsBox.Text);
            foreach (string existing in tags)
                if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) return;
            tags.Add(tag);
            TagsBox.Text = string.Join(", ", tags);
        }

        /// <summary>按逗号/分号/换行拆分，去空格并忽略大小写去重。</summary>
        private static List<string> ParseTags(string text)
        {
            var tags = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tags;
            foreach (string raw in text.Split(new[] { ',', '，', ';', '；', '\n', '\r' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string tag = raw.Trim().Trim('"').Trim();
                if (tag.Length == 0) continue;
                bool duplicate = false;
                foreach (string existing in tags)
                    if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
                if (!duplicate) tags.Add(tag);
            }
            return tags;
        }

        private List<string> CollectTags() => ParseTags(TagsBox.Text);

        /// <summary>按当前发布目标预填 TAG 编辑区，避免更新已有物品时误清空。</summary>
        private void ApplyTagDefaults(PublishTarget target)
        {
            ulong id = target?.Id ?? 0;
            bool wasLoading = _loading;
            _loading = true;

            List<string> current = target?.Tags;
            if (id != 0 && (current == null || current.Count == 0) && _current != null && !_current.Mod.IsBroken)
            {
                ModMeta rememberedMeta = _meta.Get(_current.Mod.ModId);
                if (rememberedMeta.Tags != null && rememberedMeta.Tags.Count > 0 && rememberedMeta.PublishedId == id)
                    current = rememberedMeta.Tags;
            }
            bool hasExisting = current != null && current.Count > 0;

            if (id == 0)
            {
                KeepTagsCheck.IsChecked = false;
                KeepTagsCheck.IsEnabled = false;
                TagsBox.Text = "";
                TagsBox.IsEnabled = true;
                _tagHintContext = "新建物品：填写要写入的 TAG；留空表示不带 TAG。";
            }
            else
            {
                KeepTagsCheck.IsEnabled = true;
                KeepTagsCheck.IsChecked = true;
                TagsBox.Text = hasExisting ? string.Join(", ", current) : "";
                TagsBox.IsEnabled = false; // 默认保留，勾选时不可编辑
                _tagHintContext = hasExisting
                    ? "已读取工坊现有 TAG，默认保留；取消勾选后按上面的内容整组覆盖。"
                    : "未获取到工坊现有 TAG（物品不在查询结果中时可能发生）；默认保留不动，取消勾选可重设。";
            }

            _loading = wasLoading;
            UpdateTagHint();
        }

        private void ApplyKeepTags(bool? value)
        {
            bool wasLoading = _loading;
            _loading = true;
            KeepTagsCheck.IsChecked = value;
            _loading = wasLoading;
            TagsBox.IsEnabled = value != true || SelectedTargetId() == 0;
        }

        private void UpdateTagHint()
        {
            if (TagsHint == null) return;
            int count = ParseTags(TagsBox.Text).Count;
            string text = _tagHintContext ?? "";
            if (count > 0) text += " 当前 " + count + " 个。";
            if (count > MaxTags) text += " 超过 Steam 上限（" + MaxTags + " 个），请删减后再发布。";
            TagsHint.Text = text.Trim();
        }

        private void TagsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _targetSelectionDirty = true;
            UpdateTagHint();
            UpdatePublishAvailability();
        }

        private void KeepTagsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _targetSelectionDirty = true;
            bool keep = KeepTagsCheck.IsChecked == true;
            TagsBox.IsEnabled = !keep || SelectedTargetId() == 0;
            UpdatePublishAvailability();
        }

        private void VisibilityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) _targetSelectionDirty = true;
            if (!_loading) UpdatePublishAvailability();
        }

        private void KeepDescriptionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loading) _targetSelectionDirty = true;
            if (!_loading) UpdatePublishAvailability();
        }

        private ulong SelectedTargetId()
        {
            return PublishedBox.SelectedItem is PublishTarget t ? t.Id : 0;
        }

        private int? SelectedVisibility()
        {
            switch (VisibilityBox.SelectedIndex)
            {
                case 1: return 0;
                case 2: return 1;
                case 3: return 3;
                case 4: return 2;
                default: return null;
            }
        }

        // ───────────────────────── 多语言 ─────────────────────────

        private void BuildLanguages()
        {
            LangBox.ItemsSource = LanguageLabels;
            LangBox.SelectedIndex = 0;
            for (int i = 0; i < _langs.Length; i++) _langs[i] = new LangBuf();
        }

        private void LangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int next = LangBox.SelectedIndex;
            if (next < 0 || next == _langIndex) return;

            FlushLang();
            _langIndex = next;
            ShowLang(next);
        }

        private void FlushLang()
        {
            if (_langIndex < 0 || _langIndex >= _langs.Length) return;
            if (_langs[_langIndex] == null) _langs[_langIndex] = new LangBuf();
            _langs[_langIndex].Title = LangTitle.Text ?? "";
            _langs[_langIndex].Desc = LangDesc.Text ?? "";
        }

        private void ShowLang(int index)
        {
            if (index < 0 || index >= _langs.Length) return;
            LangBuf buf = _langs[index] ?? new LangBuf();
            LangTitle.Text = buf.Title ?? "";
            LangDesc.Text = buf.Desc ?? "";
        }

        private void FillEnglishBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.Mod.Manifest == null) return;

            FlushLang();
            _langs[0].Title = _current.Mod.Manifest.Name;
            _langs[0].Desc = _current.Mod.Manifest.Description;

            _langIndex = 0;
            if (LangBox.SelectedIndex != 0) LangBox.SelectedIndex = 0;
            ShowLang(0);
            AppendLog("已用清单内容填充 English。");
        }

        private async Task LoadPreviewChangeIconAsync()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "change-preview.svg");
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var source = new SvgImageSource();
                SvgImageSourceLoadStatus status = await source.SetSourceAsync(stream);
                if (status != SvgImageSourceLoadStatus.Success)
                    throw new InvalidDataException("SVG 加载失败：" + status);
                PreviewChangeIcon.Source = source;
                PreviewIconFallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppendLog("预览图标使用备用样式：" + ex.Message);
            }
        }

        private void PreviewPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _previewPointerOver = true;
            UpdatePreviewOverlay();
        }

        private void PreviewPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _previewPointerOver = false;
            UpdatePreviewOverlay();
        }

        private void PreviewFocusChanged(object sender, RoutedEventArgs e) => UpdatePreviewOverlay();

        private void UpdatePreviewOverlay()
        {
            if (PickPreviewBtn == null || PreviewHoverOverlay == null) return;
            bool keyboardFocus = PickPreviewBtn.FocusState == FocusState.Keyboard ||
                                 PickPreviewBtn.FocusState == FocusState.Programmatic;
            PreviewHoverOverlay.Visibility = PickPreviewBtn.IsEnabled && (_previewPointerOver || keyboardFocus)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PickPreviewBtn_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var fi = new FileInfo(file.Path);
            if (fi.Length > 1024 * 1024)
            {
                if (!await ConfirmAsync($"该图 {fi.Length / 1024} KB，超过 Steam 建议的 1MB。仍要使用吗？", "图片过大"))
                    return;
            }

            _previewPath = file.Path;
            ImageSource picked = SafeImage(_previewPath);
            PreviewImage.Source = picked;
            PreviewHint.Visibility = picked == null ? Visibility.Visible : Visibility.Collapsed;
        }

        // ───────────────────────── 发布 ─────────────────────────

        private async void UploadBtn_Click(object sender, RoutedEventArgs e) => await StartPublishAsync(true);

        private async void MetaOnlyBtn_Click(object sender, RoutedEventArgs e) => await StartPublishAsync(false);

        private async Task StartPublishAsync(bool withContent)
        {
            if (_uploading) return;
            if (_current == null || _current.Mod.Manifest == null)
            {
                if (_current?.Mod.ManifestResult != null && _current.Mod.ManifestResult.HasErrors)
                {
                    await ShowManifestIssuesAsync(_current.Mod.ManifestResult);
                    return;
                }
                await WarnAsync("请先选择一个清单有效的模组。");
                return;
            }
            if (!_steam.Ready)
            {
                await WarnAsync("Steam 尚未初始化，无法发布。");
                return;
            }
            if (!_steam.IsLoggedOn())
            {
                await WarnAsync("Steam 客户端未连接。请确认 Steam 在线；如果之前用 steamcmd 登录过，请退出并重启 Steam。");
                return;
            }

            ulong published = SelectedTargetId();
            if (!withContent && published == 0)
            {
                await WarnAsync("「仅更新页面」需要选一个已发布的物品。");
                return;
            }
            int? visibility = SelectedVisibility();
            if (published == 0 && !visibility.HasValue)
            {
                await WarnAsync("新建物品时请选择可见性。");
                return;
            }

            List<LangPayload> languages = CollectLanguages();
            if (languages.Count == 0)
            {
                await WarnAsync("至少填写一种语言的标题或描述。");
                return;
            }

            List<string> tags = CollectTags();
            if (tags.Count > MaxTags)
            {
                await WarnAsync($"TAG 最多 {MaxTags} 个，当前填写了 {tags.Count} 个，请删减后再发布。");
                return;
            }
            bool applyTags = published != 0 ? KeepTagsCheck.IsChecked != true : tags.Count > 0;

            ModManifestInfo manifest = _current.Mod.Manifest;
            string files = "不重传模组文件";
            if (withContent)
            {
                try { files = Directory.GetFiles(_current.Mod.Folder, "*", SearchOption.AllDirectories).Length + " 个文件"; }
                catch { files = "无法统计"; }
            }
            bool preserveDescription = published != 0 && KeepDescriptionCheck.IsChecked == true;
            string sourceDetail = withContent ? $"来源文件夹：\n{_current.Mod.Folder}\n\n" : "本次不重传模组文件。\n\n";
            if (!await ConfirmAsync(
                    $"将更新 {manifest.Name} v{manifest.Version}（{files}）。\n\n" +
                    sourceDetail +
                    $"物品：{(published == 0 ? "新建" : published.ToString())}\n" +
                    $"可见性：{VisibilityLabel(visibility)}\n" +
                    $"描述：{(preserveDescription ? "保留工坊现有描述" : "使用填写的描述")}\n" +
                    $"TAG：{DescribeTags(applyTags, tags, published)}\n" +
                    $"语言：{languages.Count} 种\n\n确认开始？", "确认发布"))
                return;

            SaveCurrent();
            ShowLog();

            var plan = new PublishPlan
            {
                PublishedFileId = published,
                ContentFolder = withContent ? _current.Mod.Folder : "",
                PreviewFile = _previewPath ?? "",
                Visibility = visibility,
                PreserveDescription = preserveDescription,
                ChangeNote = (ChangeNoteBox.Text ?? "").Trim(),
                KeyValueId = manifest.Id,
                KeyValueVersion = manifest.Version,
                Tags = tags,
                ApplyTags = applyTags,
                Languages = languages,
            };

            _publishingModId = manifest.Id;
            _publishingWasNew = published == 0;
            SetBusy(true);
            UploadProgress.Value = 0;
            StatusText.Text = "开始…";
            AppendLog("—— " + (withContent ? "完整发布" : "仅更新页面") + "：" +
                      manifest.Name + " v" + manifest.Version + " ——");
            _steam.Publish(plan);
        }

        private static string DescribeTags(bool applyTags, List<string> tags, ulong publishedId)
        {
            if (!applyTags) return "保留工坊现有 TAG";
            if (tags == null || tags.Count == 0)
                return publishedId == 0 ? "无（不带 TAG）" : "清空现有 TAG";
            return string.Join("、", tags);
        }

        private List<LangPayload> CollectLanguages()
        {
            FlushLang();

            var list = new List<LangPayload>();
            for (int i = 0; i < LanguageKeys.Length; i++)
            {
                string title = (_langs[i]?.Title ?? "").Trim();
                string desc = (_langs[i]?.Desc ?? "").Trim();
                if (title.Length == 0 && desc.Length == 0) continue;
                list.Add(new LangPayload { SteamLang = LanguageKeys[i], Title = title, Desc = desc });
            }

            if (list.Count == 0 || list[0].SteamLang != "english")
            {
                if (_current != null && _current.Mod.Manifest != null)
                {
                    list.Insert(0, new LangPayload
                    {
                        SteamLang = "english",
                        Title = _current.Mod.Manifest.Name,
                        Desc = _current.Mod.Manifest.Description,
                    });
                }
            }
            return list;
        }

        private void OnPublishCompleted(bool ok, string message, ulong id)
        {
            SetBusy(false);
            UploadProgress.Value = ok ? 100 : UploadProgress.Value;
            StatusText.Text = ok ? "Steam 已确认提交" : "发布失败";

            if (ok)
            {
                AppendLog("✔ " + message + "，物品 ID " + id);
                AppendLog("页面：https://steamcommunity.com/sharedfiles/filedetails/?id=" + id);
                ModMeta meta = _meta.Get(_publishingModId);
                if (meta.PublishedId == id) meta.PendingFirstPublish = false;
                ApplyKeepDescription(true);
                SaveCurrent();
                RefreshTargets();
            }
            else
            {
                AppendLog("✘ " + message);
            }
            _publishingModId = "";
            _publishingWasNew = false;
        }

        private void OnPublishUnconfirmed(string message, ulong id)
        {
            SetBusy(false);
            StatusText.Text = "提交结果未确认";
            AppendLog("⚠ " + message);
            if (id != 0) AppendLog("物品页面：https://steamcommunity.com/sharedfiles/filedetails/?id=" + id);
            ShowLog();
            _publishingModId = "";
            _publishingWasNew = false;
        }

        private void SetBusy(bool busy)
        {
            _uploading = busy;
            UploadBtn.IsEnabled = !busy;
            MetaOnlyBtn.IsEnabled = !busy;
            RefreshPublishedBtn.IsEnabled = !busy;
            RescanBtn.IsEnabled = !busy;
            BrowseBtn.IsEnabled = !busy;
            RetryBtn.IsEnabled = !busy;
            ModList.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            PublishedBox.IsEnabled = !busy;
            UsePublishedIdBtn.IsEnabled = !busy;
            ImportVdfBtn.IsEnabled = !busy;
            VisibilityBox.IsEnabled = !busy;
            KeepDescriptionCheck.IsEnabled = !busy;
            ChangeNoteBox.IsEnabled = !busy;
            LangBox.IsEnabled = !busy;
            LangTitle.IsEnabled = !busy;
            LangDesc.IsEnabled = !busy;
            FillEnglishBtn.IsEnabled = !busy;
            PickPreviewBtn.IsEnabled = !busy;
            KeepTagsCheck.IsEnabled = !busy && SelectedTargetId() != 0;
            TagsBox.IsEnabled = !busy && !(SelectedTargetId() != 0 && KeepTagsCheck.IsChecked == true);
            TagSuggestionPanel.IsHitTestVisible = !busy;
            UpdatePreviewOverlay();
            UpdatePublishAvailability();
        }

        private void UpdatePublishAvailability()
        {
            if (UploadBtn == null || MetaOnlyBtn == null || ReadinessText == null || PublishedBox == null) return;
            bool validMod = _current != null && _current.Mod.Manifest != null;
            bool online = _steam.IsLoggedOn();
            bool existing = SelectedTargetId() != 0;
            bool tooManyTags = TagsBox != null && ParseTags(TagsBox.Text).Count > MaxTags;
            UploadBtn.IsEnabled = !_uploading && validMod && online && !tooManyTags;
            MetaOnlyBtn.IsEnabled = !_uploading && validMod && online && existing && !tooManyTags;

            ReadinessText.Text = _uploading ? "正在处理，请等待完成。"
                : !validMod ? "选择一个清单有效的模组后继续。"
                : !online ? "请确认 Steam 已登录并在线。"
                : tooManyTags ? $"TAG 超过 {MaxTags} 个，请删减后再发布。"
                : !existing ? "已选择新建物品；可在发布前确认可见性。"
                : "发布前请核对目标、描述和可见性。";
        }

        private static string VisibilityLabel(int? v)
        {
            switch (v)
            {
                case 0: return "公开";
                case 1: return "仅好友";
                case 3: return "未列出";
                case 2: return "隐藏";
                default: return "保持工坊现有可见性";
            }
        }

        // ───────────────────────── 日志 ─────────────────────────

        private void ShowLog()
        {
            if (LogPanel.Visibility == Visibility.Visible) return;
            LogPanel.Visibility = Visibility.Visible;
            UpdateLogHeight();
            LogToggleBtn.Content = "收起日志";
        }

        private void UpdateLogHeight()
        {
            double maxHeight = _compactLayout == true ? 90 : 150;
            LogRow.Height = new GridLength(Math.Min(maxHeight, Math.Max(80, Root.ActualHeight * 0.16)));
        }

        private void LogToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (LogPanel.Visibility != Visibility.Visible)
            {
                ShowLog();
                return;
            }
            LogPanel.Visibility = Visibility.Collapsed;
            LogRow.Height = new GridLength(0);
            LogToggleBtn.Content = "查看日志";
        }

        private void AppendLog(string line)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => AppendLog(line));
                return;
            }

            if (line.StartsWith("✘", StringComparison.Ordinal))
                ShowLog();

            LogBox.Text += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
            LogBox.SelectionStart = LogBox.Text.Length;
        }

        private void ClearLogBtn_Click(object sender, RoutedEventArgs e) => LogBox.Text = "";

        // ───────────────────────── 杂项 ─────────────────────────

        private async Task ShowFirstCommunityNoticeAsync()
        {
            if (_communityNoticeShown || _meta.CommunityNoticeSeen) return;
            _communityNoticeShown = true;
            try { await ShowAboutAsync(true); }
            catch (Exception ex)
            {
                _communityNoticeShown = false;
                AppendLog("✘ 无法显示欢迎提示：" + ex.Message);
                return;
            }
            if (!_meta.RememberCommunityNotice())
                AppendLog("✘ " + _meta.LastError);
        }

        private async void AboutBtn_Click(object sender, RoutedEventArgs e) => await ShowAboutAsync(false);

        private async Task ShowAboutAsync(bool welcome)
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "Probably Stolen 创意工坊上传器",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = "版本：" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知"),
            });
            content.Children.Add(new TextBlock { Text = "作者：千川白浪(Nwflower)", IsTextSelectionEnabled = true });
            content.Children.Add(new TextBlock
            {
                Text = "Copyright © 2026 千川白浪(Nwflower)\n原创代码：GNU AGPL v3（AGPL-3.0-only），不提供任何担保。\n许可、第三方声明与对应源码见分发包 docs 文件夹。",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            content.Children.Add(new TextBlock { Text = "QQ群：1132567943", IsTextSelectionEnabled = true });
            if (welcome)
                content.Children.Add(new TextBlock { Text = "欢迎加入 QQ 群交流与反馈。", TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                Title = welcome ? "欢迎使用" : "关于",
                Content = content,
                CloseButtonText = welcome ? "知道了" : "关闭",
                XamlRoot = Root.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async void ManifestIssuesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Mod.ManifestResult == null) return;
            await ShowManifestIssuesAsync(_current.Mod.ManifestResult);
        }

        /// <summary>取主题画刷；资源缺失时退回指定颜色，避免换主题时抛 KeyNotFound。</summary>
        private static Brush BrushResource(string key, Windows.UI.Color fallback)
        {
            try
            {
                if (Application.Current.Resources[key] is Brush brush) return brush;
            }
            catch { }
            return new SolidColorBrush(fallback);
        }

        /// <summary>清单有错误时显示的位置摘要（前几条），完整报告在明细对话框里。</summary>
        private static string DescribeBrokenManifest(DiscoveredMod mod)
        {
            ManifestLoadResult result = mod.ManifestResult;
            if (result == null) return mod.LoadError;

            int errors = result.Errors.Count();
            int warnings = result.Warnings.Count();
            var sb = new StringBuilder();
            sb.Append(errors > 0 ? $"清单校验未通过：{errors} 个错误" : "清单校验未通过");
            if (warnings > 0) sb.Append($"、{warnings} 个警告");
            sb.AppendLine("：");
            foreach (ManifestIssue issue in result.Issues.Take(4))
            {
                sb.Append("· [").Append(issue.Level == ManifestIssueLevel.Error ? "错误" : "警告").Append("] ")
                  .Append(issue.Position);
                if (issue.Element.Length > 0) sb.Append(' ').Append(issue.Element);
                sb.AppendLine().Append("  ").AppendLine(issue.Message);
            }
            if (result.Issues.Count > 4)
                sb.Append("· 还有 ").Append(result.Issues.Count - 4).Append(" 个问题，开始上传时会列出全部");
            return sb.ToString().TrimEnd();
        }

        private static string DescribeValidManifest(DiscoveredMod mod)
        {
            ModManifestInfo manifest = mod.Manifest;
            string text = $"ID {manifest.Id} · v{manifest.Version} · {manifest.Author} · 程序集 {manifest.Assemblies.Length} 个";
            if (mod.ManifestWarningCount > 0) text += $" · {mod.ManifestWarningCount} 个警告";
            return text;
        }

        /// <summary>弹出清单问题明细：按行号逐条说明哪里不对、期望什么、怎么改，可选中复制。</summary>
        private async Task ShowManifestIssuesAsync(ManifestLoadResult result)
        {
            int errors = result.Errors.Count();
            int warnings = result.Warnings.Count();

            var body = new StackPanel { Spacing = 8, MaxWidth = 640 };
            body.Children.Add(new TextBlock
            {
                Text = errors > 0
                    ? $"游戏加载器会拒绝这个模组（{errors} 个错误、{warnings} 个警告）。下面按文件位置逐条列出问题和改法："
                    : $"这个模组可以加载，但有 {warnings} 个警告：",
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 380,
                Content = new TextBlock
                {
                    Text = result.FormatReport(),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                },
            });

            var dialog = new ContentDialog
            {
                Title = errors > 0 ? "manifest.xml 校验未通过" : "manifest.xml 有警告",
                Content = body,
                PrimaryButtonText = "复制到日志",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot,
            };
            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
            {
                AppendLog(result.FormatReport());
                ShowLog();
            }
        }

        private async Task WarnAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "提示",
                Content = message,
                CloseButtonText = "好",
                XamlRoot = Root.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ConfirmAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static ImageSource SafeImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                return new BitmapImage(new Uri(path, UriKind.Absolute));
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class LangBuf
    {
        public string Title = "";
        public string Desc = "";
    }

    /// <summary>"发布到"下拉的一项：Id = 0 表示新建。</summary>
    internal sealed class PublishTarget
    {
        public ulong Id;
        public string Title = "";
        public string ModId = "";
        public List<string> Tags = new List<string>();
        public int Visibility = -1; // -1 = 未知（新建项）

        public string Display => Id == 0
            ? "＋ 新建物品"
            : "#" + Id + "  " + (string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title);
    }

    public sealed class ModRow : INotifyPropertyChanged
    {
        public DiscoveredMod Mod { get; }

        public ModRow(DiscoveredMod mod)
        {
            Mod = mod;
            if (!string.IsNullOrEmpty(mod.PreviewPath) && File.Exists(mod.PreviewPath))
            {
                try { PreviewSource = new BitmapImage(new Uri(mod.PreviewPath, UriKind.Absolute)); }
                catch { }
            }
        }

        public string Name => Mod.Name;
        public string Subtitle => Mod.Subtitle;
        public ImageSource PreviewSource { get; }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
