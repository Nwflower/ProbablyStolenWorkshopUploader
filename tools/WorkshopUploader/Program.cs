using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Steamworks;

namespace ProbablyStolenWorkshopUploader
{
    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [STAThread]
        private static void Main(string[] args)
        {
            // 未打包的 WinUI 应用默认可能不是 Per-Monitor V2，导致窗口与 XAML 尺寸不一致。
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            if (args != null && args.Length > 0 &&
                args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            {
                Cli.SelfTest();
                return;
            }

            if (args != null && args.Length > 0 &&
                args[0].Equals("--querytest", StringComparison.OrdinalIgnoreCase))
            {
                Cli.QueryTest();
                return;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
    }

    /// <summary>无界面自检：--selftest / --querytest。</summary>
    internal static class Cli
    {
        public static void SelfTest()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log");
            int code;
            try
            {
                bool ok = SteamAPI.Init();
                if (!ok)
                {
                    File.WriteAllText(logPath,
                        "INIT FAILED" + Environment.NewLine +
                        "steam_api64.dll 是否在 exe 同目录：" + File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_api64.dll")) + Environment.NewLine +
                        "steam_appid.txt 是否在 exe 同目录：" + File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam_appid.txt")) + Environment.NewLine);
                    code = 2;
                }
                else
                {
                    bool loggedOn = false;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 6000)
                    {
                        SteamAPI.RunCallbacks();
                        loggedOn = SteamUser.BLoggedOn();
                        if (loggedOn) break;
                        Thread.Sleep(50);
                    }

                    File.WriteAllText(logPath,
                        "OK" + Environment.NewLine +
                        "user=" + SteamFriends.GetPersonaName() + Environment.NewLine +
                        "appid=" + SteamUtils.GetAppID().m_AppId + Environment.NewLine +
                        "loggedOn=" + loggedOn + Environment.NewLine +
                        "waitedMs=" + sw.ElapsedMilliseconds + Environment.NewLine);
                    SteamAPI.Shutdown();
                    code = loggedOn ? 0 : 4;
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(logPath, "EXCEPTION " + ex); } catch { }
                code = 3;
            }
            Environment.Exit(code);
        }

        public static void QueryTest()
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "querytest.log");
            var sb = new System.Text.StringBuilder();
            int code = 0;
            try
            {
                if (!SteamAPI.Init())
                {
                    File.WriteAllText(logPath, "INIT FAILED");
                    Environment.Exit(2);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 5000)
                {
                    SteamAPI.RunCallbacks();
                    Thread.Sleep(50);
                    if (SteamUser.BLoggedOn()) break;
                }

                ulong steamId = SteamUser.GetSteamID().m_SteamID;
                uint accountId = (uint)(steamId & 0xFFFFFFFF);
                sb.AppendLine("steamId=" + steamId);
                sb.AppendLine("accountId=" + accountId);
                sb.AppendLine("loggedOn=" + SteamUser.BLoggedOn());

                var appId = new AppId_t(SteamUploader.DefaultAppId);
                UGCQueryHandle_t handle = SteamUGC.CreateQueryUserUGCRequest(
                    new AccountID_t(accountId),
                    EUserUGCList.k_EUserUGCList_Published,
                    EUGCMatchingUGCType.k_EUGCMatchingUGCType_Items,
                    EUserUGCListSortOrder.k_EUserUGCListSortOrder_CreationOrderDesc,
                    appId, appId, 1);

                sb.AppendLine("queryHandleValid=" + (handle != UGCQueryHandle_t.Invalid));

                SteamAPICall_t call = SteamUGC.SendQueryUGCRequest(handle);
                bool finished = false;
                SteamUGCQueryCompleted_t completed = default;
                var result = CallResult<SteamUGCQueryCompleted_t>.Create((r, ioFail) =>
                {
                    completed = r;
                    finished = true;
                });
                result.Set(call);

                sw.Restart();
                while (!finished && sw.ElapsedMilliseconds < 8000)
                {
                    SteamAPI.RunCallbacks();
                    Thread.Sleep(50);
                }

                if (!finished)
                {
                    sb.AppendLine("QUERY TIMEOUT");
                    code = 5;
                }
                else
                {
                    sb.AppendLine("queryResult=" + completed.m_eResult);
                    sb.AppendLine("numResults=" + completed.m_unNumResultsReturned);
                    for (uint i = 0; i < completed.m_unNumResultsReturned; i++)
                    {
                        if (SteamUGC.GetQueryUGCResult(completed.m_handle, i, out SteamUGCDetails_t d))
                            sb.AppendLine($"  [{i}] id={d.m_nPublishedFileId.m_PublishedFileId} title={d.m_rgchTitle}");
                    }
                    code = completed.m_eResult == EResult.k_EResultOK ? 0 : 6;
                }

                SteamUGC.ReleaseQueryUGCRequest(handle);
                SteamAPI.Shutdown();
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                code = 3;
            }
            File.WriteAllText(logPath, sb.ToString());
            Environment.Exit(code);
        }
    }
}
