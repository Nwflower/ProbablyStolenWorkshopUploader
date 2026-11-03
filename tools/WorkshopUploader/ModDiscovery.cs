using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProbablyStolenWorkshopUploader
{
    public enum ModSource
    {
        Local,
        Workshop
    }

    /// <summary>一个可用于发布的 mod：来自游戏 Mods/ 或创意工坊订阅目录。</summary>
    public sealed class DiscoveredMod
    {
        public string Folder = "";
        public ModSource Source;
        public ulong PublishedFileId;      // 仅创意工坊来源
        public ModManifestInfo Manifest;   // 读取失败时为 null
        public ManifestLoadResult ManifestResult; // 校验明细：错误与警告都留着，供界面展示
        public string LoadError = "";
        public string PreviewPath = "";

        public int ManifestWarningCount => ManifestResult != null ? ManifestResult.Warnings.Count() : 0;

        public string Name => Manifest != null ? Manifest.Name : Path.GetFileName(Folder);
        public string Version => Manifest != null ? Manifest.Version : "";
        public string ModId => Manifest != null ? Manifest.Id : "";
        public bool IsBroken => Manifest == null;

        public string Subtitle
        {
            get
            {
                bool workshop = Source == ModSource.Workshop;
                if (IsBroken) return workshop ? "工坊 · 清单未通过校验" : "清单未通过校验";
                string version = Version.Length > 0 ? "v" + Version : "";
                if (!workshop) return version;
                return "工坊" + (version.Length > 0 ? " · " + version : "") + " · #" + PublishedFileId;
            }
        }
    }

    /// <summary>
    /// 定位游戏安装目录，并扫描本地 Mods/ 与 Steam 创意工坊订阅内容。
    /// 不依赖工作区配置，纯独立工具。
    /// </summary>
    public static class ModDiscovery
    {
        public const uint AppId = 4348910;
        public static string LastGameDirError = "";

        public static string FindGameDir()
        {
            LastGameDirError = "";
            List<string> libs = AllLibraries();
            if (libs.Count == 0)
            {
                LastGameDirError = "未找到 Steam 安装目录（注册表 HKCU\\Software\\Valve\\Steam\\SteamPath）。";
                return null;
            }

            foreach (string lib in libs)
            {
                string p = Path.Combine(lib, "common", "Probably Stolen");
                if (Directory.Exists(p)) return p;
            }

            LastGameDirError = "已找到 Steam 库，但没有安装 Probably Stolen。检查过：" + string.Join("；", libs);
            return null;
        }

        /// <summary>
        /// 扫描 mod。默认只扫本机 Mods/（上传器只处理本地模组，工坊订阅的副本不列出）。
        /// 读不出清单的本地条目也会返回，便于发现问题。
        /// </summary>
        public static List<DiscoveredMod> Scan(string gameDir, bool includeWorkshop = false)
        {
            var list = new List<DiscoveredMod>();

            if (!string.IsNullOrEmpty(gameDir))
            {
                string modsDir = Path.Combine(gameDir, "Mods");
                if (Directory.Exists(modsDir))
                {
                    foreach (string dir in SafeDirs(modsDir))
                    {
                        DiscoveredMod m = LoadMod(dir, ModSource.Local, 0);
                        if (m != null) list.Add(m);
                    }
                }
            }

            if (includeWorkshop)
            {
                foreach (string lib in AllLibraries())
                {
                    string content = Path.Combine(lib, "workshop", "content", AppId.ToString());
                    if (!Directory.Exists(content)) continue;
                    foreach (string dir in SafeDirs(content))
                    {
                        ulong id = 0;
                        ulong.TryParse(Path.GetFileName(dir), out id);
                        DiscoveredMod m = LoadMod(dir, ModSource.Workshop, id);
                        if (m != null) list.Add(m);
                    }
                }
            }

            return list
                .OrderBy(m => m.Source)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static DiscoveredMod LoadMod(string folder, ModSource source, ulong publishedId)
        {
            // 工坊目录里没有 manifest.xml 的子目录通常是缓存/空壳，跳过。
            if (!File.Exists(Path.Combine(folder, "manifest.xml")) && source == ModSource.Workshop)
                return null;

            var mod = new DiscoveredMod
            {
                Folder = folder,
                Source = source,
                PublishedFileId = publishedId,
                PreviewPath = FindPreview(folder),
            };

            mod.ManifestResult = ModManifestInfo.Validate(folder);
            mod.Manifest = mod.ManifestResult.HasErrors ? null : mod.ManifestResult.Manifest;
            if (mod.Manifest == null)
            {
                mod.LoadError = mod.ManifestResult.Summary;
                if (source == ModSource.Workshop) return null; // 工坊里的坏包没有发布价值
            }

            return mod;
        }

        public static string FindPreview(string folder)
        {
            foreach (string name in new[] { "preview.png", "preview.jpg", "preview.jpeg" })
            {
                string p = Path.Combine(folder, name);
                if (File.Exists(p)) return p;
            }
            return "";
        }

        private static IEnumerable<string> SafeDirs(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return new string[0]; }
        }

        /// <summary>Steam 根目录 + libraryfolders.vdf 中登记的其他库。</summary>
        public static List<string> AllLibraries()
        {
            var result = new List<string>();
            string root = SteamRoot();
            if (root == null) return result;

            AddUnique(result, Path.Combine(root, "steamapps"));

            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string text = File.ReadAllText(vdf);
                    // "path"		"D:\\SteamLibrary"
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                    {
                        string lib = m.Groups[1].Value.Replace(@"\\", @"\");
                        AddUnique(result, Path.Combine(lib, "steamapps"));
                    }
                }
                catch { }
            }

            return result;
        }

        private static void AddUnique(List<string> list, string steamappsPath)
        {
            string norm = steamappsPath.TrimEnd('\\', '/');
            if (norm.Length == 0) return;
            if (!Directory.Exists(norm)) return;
            if (list.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(norm);
        }

        private static string SteamRoot()
        {
            foreach (string key in new[]
                     {
                         @"HKEY_CURRENT_USER\Software\Valve\Steam",
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
                         @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                     })
            {
                try
                {
                    object v = Registry.GetValue(key, "SteamPath", null) ?? Registry.GetValue(key, "InstallPath", null);
                    string s = v as string;
                    if (!string.IsNullOrWhiteSpace(s) && Directory.Exists(s)) return s.Replace('/', '\\');
                }
                catch { }
            }
            return null;
        }
    }
}
