using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;

namespace ProbablyStolenWorkshopUploader
{
    public sealed class LangPayload
    {
        public string SteamLang = "english";
        public string Title = "";
        public string Desc = "";
    }

    public sealed class PublishPlan
    {
        public uint AppId = SteamUploader.DefaultAppId;
        public ulong PublishedFileId;              // 0 = 新建
        public string ContentFolder = "";          // 空 = 只改描述与预览，不重传内容
        public string PreviewFile = "";
        // null = 不修改已有物品的可见性；新建物品必须明确指定。
        public int? Visibility;
        public bool PreserveDescription = true;
        public string ChangeNote = "";
        public string KeyValueId = "";
        public string KeyValueVersion = "";
        // 工坊分类 TAG（物品级，不分语言）。ApplyTags=false 表示本次不写 TAG，
        // 已有物品保持工坊现状；为 true 时用 Tags 整组覆盖（空列表即清空）。
        public List<string> Tags = new List<string>();
        public bool ApplyTags;
        public List<LangPayload> Languages = new List<LangPayload>();

        public bool UploadContent => !string.IsNullOrWhiteSpace(ContentFolder);
    }

    public sealed class WorkshopItemInfo
    {
        public ulong Id;
        public string Title = "";
        public string ModId = "";
        public List<string> Tags = new List<string>();
        public ERemoteStoragePublishedFileVisibility Visibility;

        public string Display
        {
            get
            {
                string vis;
                switch (Visibility)
                {
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic: vis = "公开"; break;
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly: vis = "仅好友"; break;
                    case ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityUnlisted: vis = "未列出"; break;
                    default: vis = "隐藏"; break;
                }
                string title = string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title;
                return "#" + Id + "   " + title + "   · " + vis;
            }
        }
    }

    /// <summary>
    /// SteamUGC 发布封装。
    ///
    /// 调用链：
    ///   SteamAPI.Init → 界面定时器 RunCallbacks
    ///   新建：CreateItem → CreateItemResult_t
    ///   每一轮语言：StartItemUpdate → SetItemUpdateLanguage → SetItemTitle/Description
    ///               （仅第一轮附带 SetItemContent/Preview/Visibility/KeyValueTag）
    ///               → SubmitItemUpdate → SubmitItemUpdateResult_t
    ///   期间 GetItemUpdateProgress 轮询进度。
    ///
    /// 关键点：不调用 SetItemContent 的那一轮只写元数据，工坊内容保持不变——
    /// 这是"只改预览图和描述"的实现方式。多语言靠 SetItemUpdateLanguage 逐轮提交。
    /// 所有 Steam 调用必须发生在同一线程（界面定时器泵 RunCallbacks）。
    /// </summary>
    public sealed class SteamUploader
    {
        public const uint DefaultAppId = 4348910; // Probably Stolen 正式版

        public event Action<string> Log;
        public event Action<string> Failed;
        public event Action<ulong> Published;   // 拿到物品 ID
        public event Action<bool, string, ulong> Completed;
        public event Action<string, ulong> Unconfirmed;
        public event Action<List<WorkshopItemInfo>> QueryCompleted;

        private CallResult<CreateItemResult_t> _createResult;
        private CallResult<SubmitItemUpdateResult_t> _submitResult;
        private CallResult<SteamUGCQueryCompleted_t> _queryResult;

        private UGCUpdateHandle_t _updateHandle = UGCUpdateHandle_t.Invalid;
        private PublishedFileId_t _publishedId = PublishedFileId_t.Invalid;
        private PublishPlan _plan;
        private int _langIndex;
        private DateTime _nextRoundAt = DateTime.MinValue;
        private List<WorkshopItemInfo> _queryItems;
        private uint _queryAccountId;
        private uint _queryPage;
        private uint _queryReceived;
        private bool _queryInProgress;
        private UGCQueryHandle_t _activeQueryHandle = UGCQueryHandle_t.Invalid;
        private readonly Func<DateTime> _utcNow;
        private static readonly TimeSpan SubmitStallLimit = TimeSpan.FromMinutes(2);
        private bool _awaitingSubmission;
        private bool _callbackErrorLogged;
        private DateTime _lastProgressAt;
        private EItemUpdateStatus _lastProgressStatus;
        private ulong _lastProgressDone;
        private ulong _lastProgressTotal;

        public SteamUploader(Func<DateTime> utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool Ready { get; private set; }
        public bool Busy { get; private set; }

        public bool Init(uint appId)
        {
            try
            {
                if (!SteamAPI.Init())
                {
                    Failed?.Invoke("SteamAPI.Init 失败。请确认：①Steam 客户端已登录 ②账号拥有 AppID " + appId + " ③steam_appid.txt 内容正确。");
                    return false;
                }
                Ready = true;
                SteamAPI.RunCallbacks();

                string user = SteamFriends.GetPersonaName();
                Log?.Invoke("Steam 已初始化，账号：" + user + "，AppID：" + SteamUtils.GetAppID().m_AppId);
                return true;
            }
            catch (Exception ex)
            {
                Failed?.Invoke("Steam 初始化异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>由界面定时器驱动：处理回调与语言轮次之间的间隔。</summary>
        public void Pump()
        {
            if (!Ready) return;
            try { SteamAPI.RunCallbacks(); }
            catch (Exception ex)
            {
                if (!_callbackErrorLogged)
                {
                    _callbackErrorLogged = true;
                    Log?.Invoke("Steam 回调处理异常：" + ex.Message);
                }
            }

            if (Busy && _awaitingSubmission)
            {
                try
                {
                    EItemUpdateStatus status = SteamUGC.GetItemUpdateProgress(_updateHandle, out ulong done, out ulong total);
                    if (status != _lastProgressStatus || done != _lastProgressDone || total != _lastProgressTotal)
                    {
                        _lastProgressAt = _utcNow();
                        _lastProgressStatus = status;
                        _lastProgressDone = done;
                        _lastProgressTotal = total;
                    }
                }
                catch { /* 暂时读不到进度时仍等待回调，超时后提示结果未知。 */ }

                if (_utcNow() - _lastProgressAt >= SubmitStallLimit)
                {
                    // 只注销本地回调，不代表取消 Steam 服务端已经收到的提交。
                    try { _submitResult?.Cancel(); } catch { }
                    _submitResult = null;
                    _awaitingSubmission = false;
                    Busy = false;
                    _updateHandle = UGCUpdateHandle_t.Invalid;
                    Unconfirmed?.Invoke("超过 2 分钟没有新进度，Steam 未返回最终确认。文件或部分文案可能已经提交，请先检查工坊页面和更新记录，再决定是否重试。", _publishedId.m_PublishedFileId);
                    return;
                }
            }

            if (Busy && _nextRoundAt != DateTime.MinValue && _utcNow() >= _nextRoundAt)
            {
                _nextRoundAt = DateTime.MinValue;
                BeginRound();
            }
        }

        /// <summary>
        /// 等待登录回调。返回 true 表示已登录。
        /// 踩过的坑：用 steamcmd 以同一账号登录会把 Steam 客户端顶下线（Session Replaced），
        /// 此后客户端停在断连状态，独立程序的 SteamUGC 调用会返回 k_EResultNoConnection。
        /// </summary>
        public bool WaitForLogon(int timeoutMs = 6000)
        {
            if (!Ready) return false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Pump();
                bool on = false;
                try { on = SteamUser.BLoggedOn(); } catch { }
                if (on) return true;
                System.Threading.Thread.Sleep(50);
            }
            return false;
        }

        public void Shutdown()
        {
            if (!Ready) return;
            if (_queryInProgress)
            {
                try { _queryResult?.Cancel(); } catch { }
                if (_activeQueryHandle != UGCQueryHandle_t.Invalid)
                    try { SteamUGC.ReleaseQueryUGCRequest(_activeQueryHandle); } catch { }
                _activeQueryHandle = UGCQueryHandle_t.Invalid;
                _queryItems = null;
                _queryInProgress = false;
            }
            try { SteamAPI.Shutdown(); } catch { }
            Ready = false;
        }

        public bool IsLoggedOn()
        {
            try { return Ready && SteamUser.BLoggedOn(); } catch { return false; }
        }

        // ───────────────────────────── 发布 ─────────────────────────────

        public void Publish(PublishPlan plan)
        {
            if (!Ready) { Completed?.Invoke(false, "Steam 尚未初始化", 0); return; }
            if (!IsLoggedOn())
            {
                Completed?.Invoke(false, "Steam 客户端未连接。请确认 Steam 在线；如果之前用 steamcmd 登录过，请退出并重启 Steam。", 0);
                return;
            }
            if (Busy) { Completed?.Invoke(false, "已有发布任务在进行中", _publishedId.m_PublishedFileId); return; }
            if (plan == null || plan.Languages == null || plan.Languages.Count == 0)
            {
                Completed?.Invoke(false, "没有可提交的语言内容", 0);
                return;
            }
            if (plan.PublishedFileId == 0 && !plan.Visibility.HasValue)
            {
                Completed?.Invoke(false, "新建物品时必须选择可见性。", 0);
                return;
            }
            if (plan.Visibility.HasValue && (plan.Visibility.Value < 0 || plan.Visibility.Value > 3))
            {
                Completed?.Invoke(false, "可见性无效。", plan.PublishedFileId);
                return;
            }

            _plan = plan;
            _langIndex = 0;
            _nextRoundAt = DateTime.MinValue;
            _awaitingSubmission = false;
            _callbackErrorLogged = false;
            Busy = true;
            _updateHandle = UGCUpdateHandle_t.Invalid;
            _publishedId = PublishedFileId_t.Invalid;

            if (plan.PublishedFileId == 0)
            {
                Log?.Invoke("新建工坊物品…");
                SteamAPICall_t call = SteamUGC.CreateItem(new AppId_t(plan.AppId), EWorkshopFileType.k_EWorkshopFileTypeFirst);
                _createResult = CallResult<CreateItemResult_t>.Create(OnItemCreated);
                _createResult.Set(call);
            }
            else
            {
                _publishedId = new PublishedFileId_t(plan.PublishedFileId);
                Log?.Invoke("更新已有物品 " + plan.PublishedFileId);
                Published?.Invoke(plan.PublishedFileId);
                BeginRound();
            }
        }

        private void OnItemCreated(CreateItemResult_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Busy = false;
                Completed?.Invoke(false, "CreateItem 失败：" + (ioFailure ? "IO 失败" : result.m_eResult.ToString()), 0);
                return;
            }
            if (result.m_bUserNeedsToAcceptWorkshopLegalAgreement)
                Log?.Invoke("提示：该账号尚未接受创意工坊法律协议，需要先在网页上同意一次。");

            _publishedId = result.m_nPublishedFileId;
            Log?.Invoke("已创建物品 ID：" + _publishedId.m_PublishedFileId);
            Published?.Invoke(_publishedId.m_PublishedFileId);
            BeginRound();
        }

        private void BeginRound()
        {
            if (_plan == null) { Busy = false; return; }

            if (_langIndex >= _plan.Languages.Count)
            {
                Busy = false;
                Completed?.Invoke(true, "Steam 已确认提交；请核对工坊页面和文件内容。", _publishedId.m_PublishedFileId);
                return;
            }

            LangPayload lang = _plan.Languages[_langIndex];
            bool firstRound = _langIndex == 0;

            _updateHandle = SteamUGC.StartItemUpdate(new AppId_t(_plan.AppId), _publishedId);
            if (_updateHandle == UGCUpdateHandle_t.Invalid)
            {
                Busy = false;
                Completed?.Invoke(false, "StartItemUpdate 返回无效句柄", _publishedId.m_PublishedFileId);
                return;
            }

            bool ok = true;
            if (!string.IsNullOrEmpty(lang.SteamLang))
                ok &= SteamUGC.SetItemUpdateLanguage(_updateHandle, lang.SteamLang);
            if (!string.IsNullOrWhiteSpace(lang.Title))
                ok &= SteamUGC.SetItemTitle(_updateHandle, lang.Title);
            if ((_plan.PublishedFileId == 0 || !_plan.PreserveDescription) && !string.IsNullOrWhiteSpace(lang.Desc))
                ok &= SteamUGC.SetItemDescription(_updateHandle, lang.Desc);

            if (firstRound)
            {
                // 只有第一轮写内容和预览；其余语言轮次保持内容不变。
                if (_plan.UploadContent)
                {
                    if (!Directory.Exists(_plan.ContentFolder))
                    {
                        Busy = false;
                        Completed?.Invoke(false, "内容目录不存在：" + _plan.ContentFolder, _publishedId.m_PublishedFileId);
                        return;
                    }
                    ok &= SteamUGC.SetItemContent(_updateHandle, _plan.ContentFolder);
                }

                if (!string.IsNullOrWhiteSpace(_plan.PreviewFile))
                {
                    if (!File.Exists(_plan.PreviewFile))
                    {
                        Busy = false;
                        Completed?.Invoke(false, "预览图不存在：" + _plan.PreviewFile, _publishedId.m_PublishedFileId);
                        return;
                    }
                    ok &= SteamUGC.SetItemPreview(_updateHandle, _plan.PreviewFile);
                }

                if (_plan.Visibility.HasValue)
                    ok &= SteamUGC.SetItemVisibility(_updateHandle,
                        (ERemoteStoragePublishedFileVisibility)_plan.Visibility.Value);

                if (!string.IsNullOrWhiteSpace(_plan.KeyValueId))
                    SteamUGC.AddItemKeyValueTag(_updateHandle, "ModId", _plan.KeyValueId);
                if (!string.IsNullOrWhiteSpace(_plan.KeyValueVersion))
                    SteamUGC.AddItemKeyValueTag(_updateHandle, "ModVersion", _plan.KeyValueVersion);

                // 工坊分类 TAG 是物品级字段，只在第一轮写一次；后续语言轮次不碰以免重复覆盖。
                if (_plan.ApplyTags)
                    ok &= SteamUGC.SetItemTags(_updateHandle, _plan.Tags ?? new List<string>());
            }

            if (!ok)
            {
                Busy = false;
                Completed?.Invoke(false, "设置发布字段失败，未提交。请检查标题、描述和文件路径。", _publishedId.m_PublishedFileId);
                return;
            }

            string label = string.IsNullOrEmpty(lang.SteamLang) ? "默认语言" : lang.SteamLang;
            Log?.Invoke("提交第 " + (_langIndex + 1) + "/" + _plan.Languages.Count + " 轮：" + label
                        + (firstRound && _plan.UploadContent ? "（含内容）" : "（仅文案）"));

            // WinUI TextBox 使用 CR 表示回车，统一转换为 LF 供工坊更新说明使用。
            string changeNote = (_plan.ChangeNote ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            SteamAPICall_t submit = SteamUGC.SubmitItemUpdate(_updateHandle, changeNote);
            if (submit == SteamAPICall_t.Invalid)
            {
                Busy = false;
                _updateHandle = UGCUpdateHandle_t.Invalid;
                Unconfirmed?.Invoke("Steam 未返回有效的提交句柄，无法确认结果。请先检查工坊页面和更新记录，再决定是否重试。", _publishedId.m_PublishedFileId);
                return;
            }
            _submitResult = CallResult<SubmitItemUpdateResult_t>.Create(OnSubmitted);
            _submitResult.Set(submit);
            _lastProgressStatus = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
            _lastProgressDone = 0;
            _lastProgressTotal = 0;
            _lastProgressAt = _utcNow();
            _awaitingSubmission = true;
        }

        private void OnSubmitted(SubmitItemUpdateResult_t result, bool ioFailure)
        {
            _awaitingSubmission = false;
            ulong id = _publishedId.m_PublishedFileId;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Busy = false;
                Completed?.Invoke(false, "提交失败：" + (ioFailure ? "IO 失败" : result.m_eResult.ToString()), id);
                return;
            }
            if (result.m_bUserNeedsToAcceptWorkshopLegalAgreement)
                Log?.Invoke("提示：需要先接受创意工坊法律协议，物品才会对他人可见。");

            Log?.Invoke("Steam 已确认第 " + (_langIndex + 1) + " 轮提交。");
            _langIndex++;
            // 连续提交之间留一点间隔，避免 Steam 端节流。
            _nextRoundAt = _utcNow().AddMilliseconds(1200);
        }

        /// <summary>轮询进度，供界面进度条使用。返回 0..1。</summary>
        public double GetProgress(out string statusText, out ulong done, out ulong total)
        {
            statusText = "";
            done = 0;
            total = 0;
            if (_updateHandle == UGCUpdateHandle_t.Invalid) return 0;

            try
            {
                EItemUpdateStatus status = SteamUGC.GetItemUpdateProgress(_updateHandle, out done, out total);
                statusText = status switch
                {
                    EItemUpdateStatus.k_EItemUpdateStatusPreparingConfig => "准备配置",
                    EItemUpdateStatus.k_EItemUpdateStatusPreparingContent => "准备内容",
                    EItemUpdateStatus.k_EItemUpdateStatusUploadingContent => "上传内容",
                    EItemUpdateStatus.k_EItemUpdateStatusUploadingPreviewFile => "上传预览图",
                    EItemUpdateStatus.k_EItemUpdateStatusCommittingChanges => "提交变更",
                    EItemUpdateStatus.k_EItemUpdateStatusInvalid => "",
                    _ => status.ToString(),
                };
                if (total == 0) return 0;
                return Math.Min(1.0, (double)done / total);
            }
            catch { return 0; }
        }

        public void ResetHandle()
        {
            _updateHandle = UGCUpdateHandle_t.Invalid;
        }

        // ─────────────────────── 查询我发布的物品 ───────────────────────

        public void QueryMyItems()
        {
            if (_queryInProgress) return;
            if (!IsLoggedOn())
            {
                Failed?.Invoke("Steam 未连接，无法查询已发布物品。");
                QueryCompleted?.Invoke(null);
                return;
            }

            _queryItems = new List<WorkshopItemInfo>();
            _queryReceived = 0;
            _queryPage = 1;
            _queryInProgress = true;
            try
            {
                ulong steamId = SteamUser.GetSteamID().m_SteamID;
                _queryAccountId = (uint)(steamId & 0xFFFFFFFF);
                QueryPage();
            }
            catch (Exception ex)
            {
                FinishQuery("查询异常：" + ex.Message);
            }
        }

        private void QueryPage()
        {
            UGCQueryHandle_t handle = UGCQueryHandle_t.Invalid;
            try
            {
                handle = SteamUGC.CreateQueryUserUGCRequest(
                    new AccountID_t(_queryAccountId),
                    EUserUGCList.k_EUserUGCList_Published,
                    EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items,
                    EUserUGCListSortOrder.k_EUserUGCListSortOrder_CreationOrderDesc,
                    new AppId_t(DefaultAppId), new AppId_t(DefaultAppId), _queryPage);
                if (handle == UGCQueryHandle_t.Invalid)
                {
                    FinishQuery("创建工坊查询失败。");
                    return;
                }

                _activeQueryHandle = handle;
                SteamUGC.SetReturnKeyValueTags(handle, true);
                SteamAPICall_t call = SteamUGC.SendQueryUGCRequest(handle);
                _queryResult = CallResult<SteamUGCQueryCompleted_t>.Create((r, io) => OnQueryDone(r, io, handle));
                _queryResult.Set(call);
            }
            catch (Exception ex)
            {
                if (handle != UGCQueryHandle_t.Invalid)
                    try { SteamUGC.ReleaseQueryUGCRequest(handle); } catch { }
                _activeQueryHandle = UGCQueryHandle_t.Invalid;
                FinishQuery("查询异常：" + ex.Message);
            }
        }

        private void OnQueryDone(SteamUGCQueryCompleted_t result, bool ioFailure, UGCQueryHandle_t handle)
        {
            string error = null;
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                {
                    error = "查询已发布物品失败：" + (ioFailure ? "IO 失败" : result.m_eResult.ToString());
                }
                else
                {
                    for (uint i = 0; i < result.m_unNumResultsReturned; i++)
                    {
                        if (!SteamUGC.GetQueryUGCResult(handle, i, out SteamUGCDetails_t d))
                            throw new InvalidOperationException("无法读取查询结果。");

                        string modId = "";
                        try
                        {
                            if (SteamUGC.GetQueryUGCKeyValueTag(handle, i, "ModId", out string tagValue, 256))
                                modId = tagValue ?? "";
                        }
                        catch { }

                        // 读取物品现有的工坊分类 TAG，界面据此预填，避免更新时误清空。
                        var tags = new List<string>();
                        try
                        {
                            uint tagCount = SteamUGC.GetQueryUGCNumTags(handle, i);
                            for (uint t = 0; t < tagCount; t++)
                            {
                                if (SteamUGC.GetQueryUGCTag(handle, i, t, out string tagText, 256) &&
                                    !string.IsNullOrWhiteSpace(tagText))
                                    tags.Add(tagText);
                            }
                        }
                        catch { }

                        _queryItems.Add(new WorkshopItemInfo
                        {
                            Id = d.m_nPublishedFileId.m_PublishedFileId,
                            Title = d.m_rgchTitle ?? "",
                            ModId = modId,
                            Tags = tags,
                            Visibility = d.m_eVisibility,
                        });
                    }
                    _queryReceived += result.m_unNumResultsReturned;
                    if (result.m_unNumResultsReturned == 0 && _queryReceived < result.m_unTotalMatchingResults)
                        error = "查询结果不完整，请重试。";
                }
            }
            catch (Exception ex) { error = "查询异常：" + ex.Message; }
            finally
            {
                try { SteamUGC.ReleaseQueryUGCRequest(handle); } catch { }
                _activeQueryHandle = UGCQueryHandle_t.Invalid;
            }

            if (error != null) { FinishQuery(error); return; }
            if (_queryReceived < result.m_unTotalMatchingResults)
            {
                _queryPage++;
                QueryPage();
                return;
            }
            FinishQuery(null);
        }

        private void FinishQuery(string error)
        {
            _queryInProgress = false;
            List<WorkshopItemInfo> items = _queryItems;
            _queryItems = null;
            if (error != null) Failed?.Invoke(error);
            QueryCompleted?.Invoke(error == null ? items : null);
        }
    }
}
