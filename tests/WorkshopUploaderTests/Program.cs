using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ProbablyStolenWorkshopUploader;
using Steamworks;

internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            foreach (string path in args)
            {
                WorkshopVdf vdf = WorkshopVdf.Parse(File.ReadAllText(path));
                Check(vdf.AppId == SteamUploader.DefaultAppId, "VDF 的 AppID 不匹配");
                string baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
                string content = Path.GetFullPath(Path.Combine(baseDir, vdf.ContentFolder));
                Check(ModManifestInfo.Load(content) != null, "VDF 的内容目录无效：" + ModManifestInfo.LastError);
                if (!string.IsNullOrWhiteSpace(vdf.PreviewFile))
                    Check(File.Exists(Path.GetFullPath(Path.Combine(baseDir, vdf.PreviewFile))), "VDF 的预览图不存在：" + path);
                Console.WriteLine("PASS 本地 VDF：" + path);
            }
            return 0;
        }
        Run("更新时保留每种语言的描述和工坊可见性", PreserveExistingMetadata);
        Run("明确选择后更新描述和可见性", UpdateExplicitMetadata);
        Run("多行更新说明转换换行后提交给 Steam", PreserveMultilineChangeNote);
        Run("新建 ID 在后续提交失败前持久化", RememberCreatedId);
        Run("Steam 离线及新建可见性未指定时不发起发布", RejectUnsafePublish);
        Run("字段设置失败时不提交不完整的更新", RejectIncompleteUpdate);
        Run("提交停滞时报告结果未确认，不报告成功", SubmissionTimeoutIsUnconfirmed);
        Run("上传进度变化会延长等待，最终回调仍可完成", ProgressAndCallback);
        Run("无效提交句柄不会无限等待", InvalidSubmitIsUnconfirmed);
        Run("查询读取全部页并报告中途失败", QueryAllPages);
        Run("Steam 重试时取消尚未完成的查询", CancelQueryOnShutdown);
        Run("本地记忆保存失败时保留错误并清理临时文件", ReportSaveFailure);
        Run("旧版本地记忆仍可读取", ReadLegacyMetadata);
        Run("首次提示只记录一次并保留已有物品信息", RememberCommunityNotice);
        Run("VDF 导入解析多行、转义和无效输入", ParseVdf);
        Run("TAG 在第一轮随发布写入，后续语言轮次不重复写", TagsWrittenOncePerPublish);
        Run("未请求修改时不清空已有工坊 TAG", TagsPreservedWhenNotApplied);
        Run("TAG 写入失败时不提交不完整的更新", RejectIncompleteTags);
        Run("查询会读取物品现有的工坊 TAG", QueryReadsTags);
        Run("manifest.xml 校验与游戏加载器一致", ValidateManifest);
        Console.WriteLine(_failures == 0 ? "全部通过。" : $"失败：{_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        Fake.Reset();
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failures++; Console.WriteLine("FAIL " + name + "： " + ex.Message); }
    }

    private static SteamUploader Uploader()
    {
        var uploader = new SteamUploader();
        Check(uploader.Init(SteamUploader.DefaultAppId), "初始化失败");
        return uploader;
    }

    private static PublishPlan Plan(ulong id) => new PublishPlan
    {
        PublishedFileId = id,
        ContentFolder = Path.GetTempPath(),
        Languages = new List<LangPayload>
        {
            new LangPayload { SteamLang = "english", Title = "name", Desc = "local description" }
        }
    };

    private static void PreserveExistingMetadata()
    {
        SteamUploader uploader = Uploader();
        PublishPlan plan = Plan(42);
        plan.Languages.Add(new LangPayload { SteamLang = "schinese", Title = "名称", Desc = "本地描述" });
        uploader.Publish(plan);
        Check(Fake.Calls.Contains("content"), "没有提交文件");
        Check(Fake.Calls.Contains("title:name"), "没有提交标题");
        Check(!Fake.Calls.Any(c => c.StartsWith("description:")), "英文描述被覆盖");
        Check(!Fake.Calls.Any(c => c.StartsWith("visibility:")), "可见性被覆盖");

        Fake.Complete(new SubmitItemUpdateResult_t { m_eResult = EResult.k_EResultOK });
        Thread.Sleep(1250);
        uploader.Pump();
        Check(Fake.Calls.Contains("language:schinese"), "没有提交第二种语言");
        Check(!Fake.Calls.Any(c => c.StartsWith("description:")), "第二种语言的描述被覆盖");
    }

    private static void UpdateExplicitMetadata()
    {
        SteamUploader uploader = Uploader();
        PublishPlan plan = Plan(42);
        plan.ContentFolder = "";
        plan.PreserveDescription = false;
        plan.Visibility = 0;
        uploader.Publish(plan);
        Check(Fake.Calls.Contains("description:local description"), "未提交指定描述");
        Check(Fake.Calls.Contains("visibility:0"), "未提交指定可见性");
        Check(!Fake.Calls.Contains("content"), "页面更新不应重传文件");
    }

    private static void PreserveMultilineChangeNote()
    {
        SteamUploader uploader = Uploader();
        PublishPlan plan = Plan(42);
        plan.ChangeNote = "第一行更新\r\n第二行更新\r第三行更新\n第四行更新";
        uploader.Publish(plan);
        string expected = "第一行更新\n第二行更新\n第三行更新\n第四行更新";
        Check(Fake.SubmittedNotes.SequenceEqual(new[] { expected }), "提交时更新说明的换行丢失或转换错误");
    }

    private static void RememberCreatedId()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workshop-uploader-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MetaStore(dir);
            SteamUploader uploader = Uploader();
            ulong completedId = 0;
            bool? completed = null;
            bool wasNew = true;
            uploader.Published += id => Check(store.RememberPublishedId("example", id, wasNew ? true : (bool?)null), "物品 ID 保存失败");
            uploader.Completed += (ok, message, id) => { completed = ok; completedId = id; };
            PublishPlan plan = Plan(0);
            plan.Visibility = 2;
            uploader.Publish(plan);
            Check(Fake.Calls.Contains("create"), "没有创建物品");
            Fake.Complete(new CreateItemResult_t { m_eResult = EResult.k_EResultOK, m_nPublishedFileId = new PublishedFileId_t(987654) });
            Check(Fake.Calls.Contains("description:local description"), "新建物品应写入描述");
            ModMeta saved = new MetaStore(dir).Get("example");
            Check(saved.PublishedId == 987654 && saved.PendingFirstPublish, "创建后没有立即保存 ID 和未完成状态");
            Fake.Complete(new SubmitItemUpdateResult_t { m_eResult = EResult.k_EResultFail });
            Check(completed == false && completedId == 987654, "失败时没有保留已创建的 ID");

            Fake.Calls.Clear();
            wasNew = false;
            PublishPlan retry = Plan(saved.PublishedId);
            retry.PreserveDescription = !saved.PendingFirstPublish;
            uploader.Publish(retry);
            Check(Fake.Calls.Contains("update:987654") && Fake.Calls.Contains("description:local description"),
                "首次发布失败后重试没有更新原物品并写入描述");
        }
        finally
        {
            string file = Path.Combine(dir, "workshop-meta.json");
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    private static void RejectUnsafePublish()
    {
        SteamUploader uploader = Uploader();
        bool? ok = null;
        uploader.Completed += (result, message, id) => ok = result;
        Fake.Online = false;
        PublishPlan plan = Plan(0);
        plan.Visibility = 2;
        uploader.Publish(plan);
        Check(ok == false && !Fake.Calls.Contains("create"), "离线时发起了创建");
        Fake.Online = true;
        ok = null;
        plan.Visibility = null;
        uploader.Publish(plan);
        Check(ok == false && !Fake.Calls.Contains("create"), "新建物品未指定可见性仍发起了创建");
    }

    private static void RejectIncompleteUpdate()
    {
        SteamUploader uploader = Uploader();
        bool? ok = null;
        ulong id = 0;
        uploader.Completed += (result, message, value) => { ok = result; id = value; };
        PublishPlan plan = Plan(42);
        plan.PreserveDescription = false;
        Fake.FailDescription = true;
        uploader.Publish(plan);
        Check(ok == false && id == 42 && !Fake.Calls.Contains("submit"), "字段失败后仍提交了更新");
    }

    private static void SubmissionTimeoutIsUnconfirmed()
    {
        DateTime now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var uploader = new SteamUploader(() => now);
        Check(uploader.Init(SteamUploader.DefaultAppId), "初始化失败");
        bool? completed = null;
        ulong unknownId = 0;
        uploader.Completed += (ok, message, id) => completed = ok;
        uploader.Unconfirmed += (message, id) => unknownId = id;
        uploader.Publish(Plan(42));
        now = now.AddSeconds(119);
        uploader.Pump();
        Check(uploader.Busy && unknownId == 0, "尚未超时就结束等待");
        now = now.AddSeconds(1);
        uploader.Pump();
        Check(!uploader.Busy && unknownId == 42 && completed == null, "停滞被误报为成功或失败");
        Check(Fake.Pending == null && Fake.Calls.Count(c => c == "submit") == 1, "超时后没有注销回调或自动重传");
    }

    private static void ProgressAndCallback()
    {
        DateTime now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var uploader = new SteamUploader(() => now);
        Check(uploader.Init(SteamUploader.DefaultAppId), "初始化失败");
        bool? completed = null;
        bool unknown = false;
        uploader.Completed += (ok, message, id) => completed = ok;
        uploader.Unconfirmed += (message, id) => unknown = true;
        uploader.Publish(Plan(42));
        Fake.ProgressStatus = EItemUpdateStatus.k_EItemUpdateStatusUploadingContent;
        Fake.ProgressTotal = 100;
        Fake.ProgressDone = 10;
        now = now.AddSeconds(100);
        uploader.Pump();
        Fake.ProgressDone = 20;
        now = now.AddSeconds(100);
        uploader.Pump();
        Check(uploader.Busy && !unknown, "有新进度时不应超时");
        now = now.AddSeconds(119);
        uploader.Pump();
        Check(uploader.Busy && !unknown, "新进度后提前超时");
        Fake.Complete(new SubmitItemUpdateResult_t { m_eResult = EResult.k_EResultOK });
        now = now.AddSeconds(2);
        uploader.Pump();
        Check(completed == true && !unknown && !uploader.Busy, "收到最终回调后没有完成");
    }

    private static void InvalidSubmitIsUnconfirmed()
    {
        SteamUploader uploader = Uploader();
        bool? completed = null;
        ulong unknownId = 0;
        uploader.Completed += (ok, message, id) => completed = ok;
        uploader.Unconfirmed += (message, id) => unknownId = id;
        Fake.InvalidSubmitCall = true;
        uploader.Publish(Plan(42));
        Check(!uploader.Busy && unknownId == 42 && completed == null && Fake.Pending == null,
            "无效句柄未报告为结果未知");
    }

    private static void QueryAllPages()
    {
        SteamUploader uploader = Uploader();
        List<WorkshopItemInfo> result = null;
        int callbacks = 0;
        uploader.QueryCompleted += items => { callbacks++; result = items; };
        Fake.Pages[1] = new List<SteamUGCDetails_t> { Item(1), Item(2) };
        Fake.Pages[2] = new List<SteamUGCDetails_t> { Item(3) };
        uploader.QueryMyItems();
        Fake.Complete(Page(1, 2, 3));
        Check(callbacks == 0 && Fake.PagesRequested.SequenceEqual(new uint[] { 1, 2 }), "未继续查询第二页");
        Fake.Complete(Page(2, 1, 3));
        Check(callbacks == 1 && result.Select(x => x.Id).SequenceEqual(new ulong[] { 1, 2, 3 }), "分页结果不完整");

        Fake.Reset();
        result = new List<WorkshopItemInfo>();
        callbacks = 0;
        Fake.Pages[1] = new List<SteamUGCDetails_t> { Item(1) };
        uploader.QueryMyItems();
        Fake.Complete(Page(1, 1, 2));
        Fake.Complete(new SteamUGCQueryCompleted_t { m_handle = new UGCQueryHandle_t(2), m_eResult = EResult.k_EResultNoConnection });
        Check(callbacks == 1 && result == null, "中途失败被误报为完整或空列表");
    }

    private static void CancelQueryOnShutdown()
    {
        SteamUploader uploader = Uploader();
        uploader.QueryMyItems();
        Check(Fake.Pending != null, "查询未开始");
        uploader.Shutdown();
        Check(Fake.Pending == null, "关闭时未取消查询");
        uploader.Init(SteamUploader.DefaultAppId);
        uploader.QueryMyItems();
        Check(Fake.PagesRequested.SequenceEqual(new uint[] { 1, 1 }), "重新连接后无法发起查询");
    }

    private static void ReportSaveFailure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workshop-uploader-tests-" + Guid.NewGuid().ToString("N"));
        string blocker = Path.Combine(dir, "workshop-meta.json");
        try
        {
            var store = new MetaStore(dir);
            Directory.CreateDirectory(blocker);
            Check(!store.RememberPublishedId("example", 7), "无法保存时报告了成功");
            Check(store.LastError.Length > 0 && store.Get("example").PublishedId == 7, "没有保留错误与内存中的 ID");
            Check(!store.RememberCommunityNotice() && !store.CommunityNoticeSeen, "首次提示保存失败时不应标为已读");
            Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "失败后留下了临时文件");
        }
        finally
        {
            if (Directory.Exists(blocker)) Directory.Delete(blocker);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    private static void ReadLegacyMetadata()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workshop-uploader-tests-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "workshop-meta.json");
        try
        {
            var store = new MetaStore(dir);
            ModMeta meta = store.Get("example");
            meta.PublishedId = 123;
            meta.For("english").Desc = "existing text";
            Check(store.Save(), "无法创建测试记忆文件");
            string json = File.ReadAllText(file);
            const string addedField = ",\"PendingFirstPublish\":false";
            Check(json.Contains(addedField), "测试数据缺少新增字段");
            File.WriteAllText(file, json.Replace(addedField, ""));

            var oldStore = new MetaStore(dir);
            ModMeta loaded = oldStore.Get("example");
            Check(loaded.PublishedId == 123 && loaded.For("english").Desc == "existing text" && !loaded.PendingFirstPublish,
                "旧版本地记忆读取不正确");
            Check(!oldStore.CommunityNoticeSeen, "旧版记忆文件不应跳过首次提示");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    private static void RememberCommunityNotice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "workshop-uploader-tests-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "workshop-meta.json");
        try
        {
            var store = new MetaStore(dir);
            Check(!store.CommunityNoticeSeen, "新用户不应跳过首次提示");
            ModMeta meta = store.Get("example");
            meta.PublishedId = 123;
            meta.For("english").Desc = "existing text";
            Check(store.RememberCommunityNotice(), "无法保存首次提示状态");

            var reopened = new MetaStore(dir);
            Check(reopened.CommunityNoticeSeen, "重新启动后未记住首次提示状态");
            Check(reopened.Get("example").PublishedId == 123 &&
                  reopened.Get("example").For("english").Desc == "existing text", "保存提示状态时丢失了已有物品信息");
            Check(reopened.RememberCommunityNotice(), "重复记录提示状态失败");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }

    private static SteamUGCDetails_t Item(ulong id) => new SteamUGCDetails_t
    {
        m_nPublishedFileId = new PublishedFileId_t(id),
        m_rgchTitle = "item " + id,
        m_eVisibility = ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate
    };

    private static SteamUGCQueryCompleted_t Page(ulong page, uint returned, uint total) => new SteamUGCQueryCompleted_t
    {
        m_handle = new UGCQueryHandle_t(page),
        m_eResult = EResult.k_EResultOK,
        m_unNumResultsReturned = returned,
        m_unTotalMatchingResults = total
    };

    private static void ParseVdf()
    {
        const string text = """
            // steamcmd
            "workshopitem"
            {
                "appid" "4348910"
                "publishedfileid" "987654"
                "contentfolder" "D:\\Build\\mods\\example"
                "previewfile" "D:\\Build\\mods\\example\\preview.png"
                "visibility" "2"
                "title" "中文 \"quoted\""
                "description" "第一行
            第二行"
                "changenote" "changes"
                "tags"
                {
                    "0" "Mod"
                    "1" "Translation"
                }
                "extra" "unsupported"
            }
            """;
        WorkshopVdf vdf = WorkshopVdf.Parse(text);
        Check(vdf.AppId == 4348910 && vdf.PublishedFileId == 987654, "ID 解析错误");
        Check(vdf.ContentFolder == @"D:\Build\mods\example", "Windows 路径转义错误");
        Check(vdf.PreviewFile.EndsWith(@"\preview.png"), "预览图路径错误");
        Check(vdf.Visibility == 2 && vdf.Title == "中文 \"quoted\"", "元数据解析错误");
        Check(vdf.Description.Contains("第一行\n第二行") && vdf.ChangeNote == "changes", "多行描述解析错误");
        Check(vdf.Tags.SequenceEqual(new[] { "Mod", "Translation" }), "嵌套 tags 段解析错误");
        Check(vdf.UnsupportedFields.SequenceEqual(new[] { "extra" }), "未提示不支持的字段");

        const string inlineTags = """
            "workshopitem"
            {
                "appid" "4348910"
                "publishedfileid" "1"
                "contentfolder" "C:\\mod"
                "tags" "Mod, Gameplay，Translation,mod"
            }
            """;
        WorkshopVdf tagVdf = WorkshopVdf.Parse(inlineTags);
        Check(tagVdf.Tags.SequenceEqual(new[] { "Mod", "Gameplay", "Translation" }), "单行 tags 解析或去重错误");
        Throws(() => WorkshopVdf.Parse(text.Replace("\"987654\"", "\"invalid\"")));
        Throws(() => WorkshopVdf.Parse(text.Replace("\"appid\" \"4348910\"", "\"appid\" \"4348910\" \"appid\" \"1\"")));
        Throws(() => WorkshopVdf.Parse(text.Replace("\"visibility\" \"2\"", "\"visibility\" \"9\"")));
        Throws(() => WorkshopVdf.Parse(text.Substring(0, text.Length - 2)));
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("无效 VDF 未被拒绝");
    }

    // ───────────────────────── 工坊 TAG ─────────────────────────

    private static void TagsWrittenOncePerPublish()
    {
        SteamUploader uploader = Uploader();
        PublishPlan plan = Plan(42);
        plan.ApplyTags = true;
        plan.Tags = new List<string> { "Mod", "Gameplay" };
        plan.Languages.Add(new LangPayload { SteamLang = "schinese", Title = "名称", Desc = "本地描述" });
        uploader.Publish(plan);
        Check(Fake.Calls.Contains("tags:Mod|Gameplay"), "第一轮没有写入 TAG");

        Fake.Complete(new SubmitItemUpdateResult_t { m_eResult = EResult.k_EResultOK });
        Thread.Sleep(1250);
        uploader.Pump();
        Check(Fake.Calls.Count(c => c == "tags:Mod|Gameplay") == 1, "第二种语言的轮次重复写入了 TAG");
    }

    private static void TagsPreservedWhenNotApplied()
    {
        SteamUploader uploader = Uploader();
        PublishPlan plan = Plan(42);
        plan.ApplyTags = false;
        plan.Tags = new List<string> { "Mod" };
        uploader.Publish(plan);
        Check(!Fake.Calls.Any(c => c.StartsWith("tags:")), "未请求修改时仍写入了 TAG");
    }

    private static void RejectIncompleteTags()
    {
        SteamUploader uploader = Uploader();
        bool? ok = null;
        ulong id = 0;
        uploader.Completed += (result, message, value) => { ok = result; id = value; };
        PublishPlan plan = Plan(42);
        plan.ApplyTags = true;
        plan.Tags = new List<string> { "Mod" };
        Fake.FailTags = true;
        uploader.Publish(plan);
        Check(ok == false && id == 42 && !Fake.Calls.Contains("submit"), "TAG 写入失败后仍提交了更新");
    }

    private static void QueryReadsTags()
    {
        SteamUploader uploader = Uploader();
        List<WorkshopItemInfo> result = null;
        uploader.QueryCompleted += items => result = items;
        Fake.Pages[1] = new List<SteamUGCDetails_t> { Item(7) };
        Fake.QueryTags[(1, 0)] = new List<string> { "Mod", "Translation" };
        uploader.QueryMyItems();
        Fake.Complete(Page(1, 1, 1));
        Check(result != null && result.Count == 1, "查询结果数量错误");
        Check(result[0].Tags.SequenceEqual(new[] { "Mod", "Translation" }), "没有读取物品现有的工坊 TAG");
    }

    // ───────────────────── manifest.xml 校验（对齐游戏 ModLoader） ─────────────────────

    private static string Body(
        string id = "my_mod",
        string assemblies = "<Assembly>MyMod.dll</Assembly>",
        string capabilities = "",
        bool withName = true,
        bool withOptionalFields = true)
    {
        var sb = new System.Text.StringBuilder();
        if (id != null) sb.AppendLine("  <ID>" + id + "</ID>");
        if (withName) sb.AppendLine("  <Name>My Mod</Name>");
        if (withOptionalFields)
        {
            sb.AppendLine("  <Author>Me</Author>");
            sb.AppendLine("  <ModVersion>1.0.0</ModVersion>");
            sb.AppendLine("  <Description>测试用</Description>");
            sb.AppendLine("  <GameVersions><Version>0.46D</Version></GameVersions>");
            sb.AppendLine("  <Prerequisites />");
        }
        if (assemblies != null) sb.AppendLine("  <Assemblies>" + assemblies + "</Assemblies>");
        if (capabilities != null) sb.AppendLine("  <Capabilities>" + capabilities + "</Capabilities>");
        return sb.ToString();
    }

    private static string ManifestDocument(string body, string root = "Manifest")
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<" + root + ">\n" + body + "</" + root + ">\n";
    }

    /// <summary>临时造一个 mod 包：manifest.xml + 一个含（或不含）IMod 标记的假 DLL。</summary>
    private static string MakeModFolder(string manifestXml, bool withImDll = true, params string[] extraDlls)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ws-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (manifestXml != null) File.WriteAllText(Path.Combine(dir, "manifest.xml"), manifestXml);
        if (withImDll)
        {
            var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes("fake assembly payload"));
            bytes.AddRange(new byte[] { 0x49, 0x4D, 0x6F, 0x64, 0x00 }); // "IMod\0"
            File.WriteAllBytes(Path.Combine(dir, "MyMod.dll"), bytes.ToArray());
        }
        foreach (string name in extraDlls)
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1, 2, 3 });
        return dir;
    }

    private static void WithMod(string manifestXml, Action<string> body, bool withImDll = true, string[] extraDlls = null)
    {
        string dir = MakeModFolder(manifestXml, withImDll, extraDlls ?? new string[0]);
        try { body(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void ValidateManifest()
    {
        // 与游戏一致：合法清单不应有任何问题
        WithMod(ManifestDocument(Body()), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(!r.HasErrors && r.Issues.Count == 0, "合法清单被误报：" + r.FormatReport());
            Check(r.Manifest.Id == "my_mod" && r.Manifest.Assemblies.Length == 1, "字段解析错误");
            Check(r.Manifest.GameVersions.Length == 1, "GameVersions 解析错误");
        });

        // 缺 <ID>
        WithMod(ManifestDocument(Body(id: null)), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.HasErrors, "缺少 ID 未被拒绝");
            Check(r.Errors.Any(e => e.Element == "<ID>" && e.Message.Contains("找不到")), "缺少 ID 的提示不明确");
        });

        // ID 含空格：要指出是第几个字符
        WithMod(ManifestDocument(Body(id: "my mod")), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.HasErrors, "非法 ID 未被拒绝");
            Check(r.Errors.Any(e => e.Message.Contains("第 3 个字符") && e.Message.Contains("' '")), "非法 ID 没有指出位置：" + r.Summary);
            Check(r.Errors.Any(e => e.Fix.Contains("my_mod")), "非法 ID 没有给出建议");
        });

        // 游戏允许大写与点号的 ID，不应被当成错误
        WithMod(ManifestDocument(Body(id: "My_Mod.2")), dir =>
            Check(!ModManifestInfo.Validate(dir).HasErrors, "游戏允许的 ID 被拒绝"));

        // 缺 <Assemblies> / <Capabilities>
        WithMod(ManifestDocument(Body(assemblies: null)), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.Errors.Any(e => e.Element == "<Assemblies>"), "缺少 Assemblies 未被拒绝");
        });
        WithMod(ManifestDocument(Body(capabilities: null)), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.Errors.Any(e => e.Element == "<Capabilities>"), "缺少 Capabilities 未被拒绝（游戏会跳过该模组）");
        });

        // 未知能力名
        WithMod(ManifestDocument(Body(capabilities: "<Capability>files</Capability><Capability>netwrok</Capability>")), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.Errors.Any(e => e.Message.Contains("netwrok") && e.Message.Contains("未知")), "未知能力名未被拒绝");
            Check(r.Errors.Any(e => e.Expect.Contains("reflection-external")), "未知能力名没有列出合法值");
        });

        // 能力名大小写按游戏归一化，不算错
        WithMod(ManifestDocument(Body(capabilities: "<Capability>Files</Capability>")), dir =>
            Check(!ModManifestInfo.Validate(dir).HasErrors, "能力名大小写被误报"));

        // 程序集路径越界 / 不是 dll
        WithMod(ManifestDocument(Body(assemblies: "<Assembly>../evil.dll</Assembly>")), dir =>
            Check(ModManifestInfo.Validate(dir).Errors.Any(e => e.Message.Contains("'..'")), "越界程序集路径未被拒绝"));
        WithMod(ManifestDocument(Body(assemblies: "<Assembly>MyMod.dllx</Assembly>")), dir =>
            Check(ModManifestInfo.Validate(dir).Errors.Any(e => e.Message.Contains(".dll")), "非 .dll 程序集未被拒绝"));

        // 列出的 DLL 不存在
        WithMod(ManifestDocument(Body(assemblies: "<Assembly>Missing.dll</Assembly>")), dir =>
            Check(ModManifestInfo.Validate(dir).Errors.Any(e => e.Message.Contains("不存在")), "缺失 DLL 未被拒绝"));

        // 列出的 DLL 里没有 IMod 实现（游戏注册后也不会加载）
        WithMod(ManifestDocument(Body()), dir =>
        {
            File.WriteAllBytes(Path.Combine(dir, "MyMod.dll"), new byte[] { 1, 2, 3, 4 }); // 存在，但没有 IMod 标记
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.Errors.Any(e => e.Message.Contains("IMod")), "没有 IMod 实现未被拒绝");
        }, withImDll: false);

        // 包里有未列入 <Assemblies> 的 DLL：游戏会注册但拒绝启用
        WithMod(ManifestDocument(Body()), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.Errors.Any(e => e.Message.Contains("没列进") && e.Actual.Contains("Extra.dll")), "未列出的 DLL 未被指出");
        }, extraDlls: new[] { "Extra.dll" });

        // XML 语法错误：要带行号
        WithMod("<?xml version=\"1.0\"?>\n<Manifest>\n  <ID>x</ID>\n", dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(r.HasErrors, "XML 语法错误未被拒绝");
            Check(r.Errors.Any(e => e.Line > 0 && e.Message.Contains("XML 语法错误")), "XML 错误没有给出行号");
        });

        // 根节点名与可选字段：游戏不拦，只警告
        WithMod(ManifestDocument(Body(withName: false, withOptionalFields: false), root: "Manifesto"), dir =>
        {
            ManifestLoadResult r = ModManifestInfo.Validate(dir);
            Check(!r.HasErrors, "游戏能加载的写法被拒绝：" + r.Summary);
            Check(r.Warnings.Any(w => w.Element.Contains("Manifesto")), "根节点名没有提示");
            Check(r.Warnings.Any(w => w.Message.Contains("<Name>")), "缺少 Name 没有提示");
        });

        // 命名空间声明会让游戏把字段当缺失
        WithMod("<?xml version=\"1.0\"?>\n<Manifest xmlns=\"urn:ps\">\n  <ID>x</ID>\n</Manifest>\n", dir =>
            Check(ModManifestInfo.Validate(dir).Errors.Any(e => e.Message.Contains("命名空间")), "命名空间没有被指出"));

        // 完全没有 manifest.xml
        string emptyDir = Path.Combine(Path.GetTempPath(), "ws-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            ManifestLoadResult r = ModManifestInfo.Validate(emptyDir);
            Check(r.Errors.Any(e => e.Message.Contains("没有 manifest.xml")), "缺少 manifest.xml 未被指出");
            Check(r.FormatReport().Contains(emptyDir), "报告里没有带路径");
        }
        finally { try { Directory.Delete(emptyDir, true); } catch { } }

        // 旧入口仍然可用：失败返回 null 并留下可读错误
        WithMod(ManifestDocument(Body(id: null)), dir =>
        {
            Check(ModManifestInfo.Load(dir) == null, "Load 未返回 null");
            Check(ModManifestInfo.LastError.Length > 0 && ModManifestInfo.LastReport.Contains("<ID>"), "LastError/LastReport 没有写入");
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
