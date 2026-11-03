using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ProbablyStolenWorkshopUploader
{
    public enum ManifestIssueLevel
    {
        Error,
        Warning,
    }

    /// <summary>
    /// manifest.xml 的一条问题。<c>Error</c> 与游戏加载器同义：该模组会被跳过或拒绝启用；
    /// <c>Warning</c> 表示游戏仍能加载，但发布前值得改。
    /// </summary>
    public sealed class ManifestIssue
    {
        public ManifestIssueLevel Level;
        public string Element = "";   // 出问题的元素，如 <ID>、<Assemblies>/<Assembly>
        public string Message = "";   // 哪里不对
        public string Expect = "";    // 期望
        public string Actual = "";    // 实际
        public string Fix = "";       // 怎么改
        public int Line;              // 0 表示取不到位置
        public int Column;

        public string Position
        {
            get
            {
                if (Line <= 0) return "位置未知";
                return Column > 0 ? $"第 {Line} 行第 {Column} 列" : $"第 {Line} 行";
            }
        }

        /// <summary>单行摘要，用于列表与日志。</summary>
        public string Summary
        {
            get
            {
                string tag = Element.Length > 0 ? " " + Element : "";
                return Position + tag + "：" + Message;
            }
        }

        public string ToReport(int index)
        {
            var sb = new StringBuilder();
            sb.Append("  ").Append(index).Append(". [")
              .Append(Level == ManifestIssueLevel.Error ? "错误" : "警告").Append("] ")
              .Append(Position);
            if (Element.Length > 0) sb.Append(" · ").Append(Element);
            sb.AppendLine();
            sb.Append("     ").AppendLine(Message);
            if (Expect.Length > 0) sb.Append("     期望：").AppendLine(Expect);
            if (Actual.Length > 0) sb.Append("     实际：").AppendLine(Actual);
            if (Fix.Length > 0) sb.Append("     改法：").AppendLine(Fix);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一次清单校验的完整结果：既给出解析后的字段，也给出全部问题（不是遇到第一个就停）。
    /// </summary>
    public sealed class ManifestLoadResult
    {
        public string FolderPath = "";
        public string ManifestPath = "";

        /// <summary>XML 解析成功时为解析结果（即使有 Error 也可用来显示名字），文件读不出时为 null。</summary>
        public ModManifestInfo Manifest;

        public readonly List<ManifestIssue> Issues = new List<ManifestIssue>();

        public IEnumerable<ManifestIssue> Errors => Issues.Where(i => i.Level == ManifestIssueLevel.Error);
        public IEnumerable<ManifestIssue> Warnings => Issues.Where(i => i.Level == ManifestIssueLevel.Warning);
        public bool HasErrors => Issues.Any(i => i.Level == ManifestIssueLevel.Error);
        public bool IsValid => !HasErrors;

        /// <summary>单行摘要，供列表与日志使用。</summary>
        public string Summary
        {
            get
            {
                if (Issues.Count == 0) return "";
                ManifestIssue first = Issues[0];
                string head = first.Message;
                return Issues.Count == 1 ? head : head + "（共 " + Issues.Count + " 个问题）";
            }
        }

        public string FormatReport()
        {
            var sb = new StringBuilder();
            string path = ManifestPath.Length > 0 ? ManifestPath : FolderPath;
            sb.Append("清单文件：").AppendLine(path);

            int errors = Errors.Count();
            int warnings = Warnings.Count();
            if (errors == 0 && warnings == 0)
            {
                sb.AppendLine("校验通过：格式与游戏加载器的要求一致。");
                return sb.ToString();
            }

            if (errors > 0)
            {
                sb.Append("游戏加载器会拒绝这个模组：").Append(errors).Append(" 个错误");
            }
            else
            {
                sb.Append("游戏可以加载，但有 ").Append(warnings).Append(" 个警告");
            }
            if (errors > 0 && warnings > 0) sb.Append("，另有 ").Append(warnings).Append(" 个警告");
            sb.AppendLine("。");
            sb.AppendLine();

            int index = 0;
            foreach (ManifestIssue issue in Issues)
                sb.Append(issue.ToReport(++index));
            sb.AppendLine();
            sb.Append("以上判据与游戏加载器一致（清单解析与启用检查），改完后重新扫描即可。");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 读取深空当铺 mod 的 manifest.xml。
    /// 字段对应游戏 ModLoader 的 <c>ModManifest</c>：ID / Name / Author / ModVersion /
    /// Description / GameVersions / Prerequisites / Assemblies / Capabilities。
    ///
    /// 校验规则逐条对齐游戏（反编译自 Assembly-CSharp 的 <c>ModLoader.ParseManifestCore</c>
    /// 与 <c>ModLoader.IsWellFormed</c>）：
    /// <list type="bullet">
    /// <item>manifest.xml 必须存在，且是可解析的 XML。</item>
    /// <item><c>&lt;ID&gt;</c> 必填，只允许字母、数字、<c>_</c>、<c>-</c>、<c>.</c>，不以 <c>.</c> 开头，最长 64 字符。</item>
    /// <item><c>&lt;Assemblies&gt;</c> 必填，至少一个 <c>&lt;Assembly&gt;</c>；路径必须是包内相对 <c>.dll</c>，不含 <c>..</c>。</item>
    /// <item><c>&lt;Capabilities&gt;</c> 必填（可为空），能力名必须已知。</item>
    /// <item>列出的 DLL 必须存在，且至少一个含 <c>IMod</c> 实现。</item>
    /// <item>包里不能有未列入 <c>&lt;Assemblies&gt;</c> 的 DLL，否则游戏拒绝启用。</item>
    /// </list>
    /// 游戏<b>不</b>校验根节点名、Name / Author / ModVersion / Description，也不按 GameVersions 拦截；
    /// 这些只作为警告，不阻止发布。
    /// </summary>
    public sealed class ModManifestInfo
    {
        /// <summary>游戏 ModLoader 识别的能力名（ModCapability.All）。</summary>
        public static readonly string[] KnownCapabilities =
        {
            "files", "network", "process", "dynamic-code", "native", "reflection-external", "steam",
        };

        public string Folder = "";
        public string Id = "";
        public string Name = "";
        public string Author = "";
        public string Version = "";
        public string Description = "";
        public string[] Assemblies = new string[0];
        public string[] GameVersions = new string[0];
        public string[] Prerequisites = new string[0];
        public string[] Capabilities = new string[0];

        /// <summary>最近一次 Load/Validate 的一行错误摘要。</summary>
        public static string LastError = "";

        /// <summary>最近一次 Load/Validate 的完整报告（含行号）。</summary>
        public static string LastReport = "";

        /// <summary>兼容旧调用：校验失败返回 null，详情见 <see cref="LastError"/> / <see cref="LastReport"/>。</summary>
        public static ModManifestInfo Load(string folder)
        {
            ManifestLoadResult result = Validate(folder);
            LastError = result.Summary;
            LastReport = result.FormatReport();
            return result.HasErrors ? null : result.Manifest;
        }

        public static ManifestLoadResult Validate(string folder)
        {
            var result = new ManifestLoadResult { FolderPath = folder ?? "" };
            try
            {
                ValidateInto(result);
            }
            catch (Exception ex)
            {
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Message = "校验清单时出现意外错误：" + ex.Message,
                });
            }
            // 错误在前、警告在后；同一级别按行号。OrderBy 是稳定排序，保留发现顺序。
            List<ManifestIssue> sorted = result.Issues
                .OrderBy(i => i.Level == ManifestIssueLevel.Error ? 0 : 1)
                .ThenBy(i => i.Line <= 0 ? int.MaxValue : i.Line)
                .ToList();
            result.Issues.Clear();
            result.Issues.AddRange(sorted);
            return result;
        }

        private static void ValidateInto(ManifestLoadResult result)
        {
            string folder = result.FolderPath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                result.Issues.Add(Error("没有指定模组文件夹", "选一个含 manifest.xml 的目录", folder, "检查 VDF 里的 contentfolder 或重新选择文件夹"));
                return;
            }

            result.ManifestPath = Path.Combine(folder, "manifest.xml");
            if (!Directory.Exists(folder))
            {
                result.Issues.Add(Error("模组文件夹不存在", "目录可用", folder, "检查路径是否拼错、磁盘是否在线"));
                return;
            }
            if (!File.Exists(result.ManifestPath))
            {
                result.Issues.Add(Error(
                    "文件夹里没有 manifest.xml",
                    "包根目录必须有 manifest.xml，与 DLL 同级",
                    folder,
                    "新建 manifest.xml；可参考模板 .agents/skills/probably-stolen-official/assets/manifest.xml.template"));
                return;
            }

            XDocument doc;
            try
            {
                doc = XDocument.Load(result.ManifestPath, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                string xmlDetail = TrimLineSuffix(ex.Message);
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "manifest.xml",
                    Message = "XML 语法错误：" + SummarizeXmlError(xmlDetail),
                    Expect = "标签成对闭合、属性值加引号、& < > 转义为 &amp; &lt; &gt;、根节点只有一个",
                    Actual = xmlDetail,
                    Fix = "按上面的行号打开该处补齐标签或引号；编辑器里未闭合的标签通常一眼可见",
                    Line = ex.LineNumber,
                    Column = ex.LinePosition,
                });
                return;
            }
            catch (Exception ex)
            {
                result.Issues.Add(Error("manifest.xml 无法读取：" + ex.Message, "文件可读", result.ManifestPath, "检查文件是否被占用、编码是否为文本"));
                return;
            }

            XElement root = doc.Root;
            if (root == null)
            {
                result.Issues.Add(Error("manifest.xml 没有根节点", "一个 <Manifest> 根节点", "空文档", "补上 <Manifest>…</Manifest>"));
                return;
            }

            var info = new ModManifestInfo { Folder = folder };
            result.Manifest = info;
            int rootLine = LineOf(root);

            // 游戏只按子元素名读取，不检查根节点名；这里降级为警告，避免拦下游戏能加载的包。
            if (!string.Equals(root.Name.LocalName, "Manifest", StringComparison.Ordinal))
            {
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Warning,
                    Element = "<" + root.Name.LocalName + ">",
                    Message = "根节点不是 <Manifest>",
                    Expect = "<Manifest>…</Manifest>",
                    Actual = "<" + root.Name.LocalName + ">…</" + root.Name.LocalName + ">",
                    Fix = "改成 <Manifest>；游戏只认子元素名，但其他工具都按 <Manifest> 解析",
                    Line = rootLine,
                    Column = ColumnOf(root),
                });
            }

            if (root.Name.Namespace != XNamespace.None)
            {
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "<" + root.Name.LocalName + ">",
                    Message = "根节点带有 XML 命名空间 xmlns=\"" + root.Name.NamespaceName + "\"，游戏按无命名空间读取子元素，会把字段全部当成缺失",
                    Expect = "去掉 xmlns 声明",
                    Fix = "删掉根节点上的 xmlns=\"…\"",
                    Line = rootLine,
                    Column = ColumnOf(root),
                });
            }

            // ── <ID>：必填 + 字符规则（ModLoader.IsValidModId） ──
            XElement idElement = root.Element("ID");
            if (idElement == null)
            {
                XElement near = ChildIgnoringCase(root, "ID");
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "<ID>",
                    Message = "找不到 <ID> 元素，游戏会跳过这个模组",
                    Expect = "<ID>my_mod</ID>（元素名区分大小写）",
                    Actual = near != null ? "找到的是 <" + near.Name.LocalName + ">" : "根节点下没有 <ID>",
                    Fix = "补上 <ID>；ID 一旦发布就不要再改，工坊按它关联本地记忆与订阅者",
                    Line = near != null ? LineOf(near) : rootLine,
                    Column = near != null ? ColumnOf(near) : ColumnOf(root),
                });
            }
            else
            {
                info.Id = (idElement.Value ?? "").Trim();
                string reason = DescribeInvalidId(info.Id);
                if (reason != null)
                {
                    result.Issues.Add(new ManifestIssue
                    {
                        Level = ManifestIssueLevel.Error,
                        Element = "<ID>",
                        Message = reason,
                        Expect = "只允许字母、数字、_、-、.，不以 . 开头，最长 64 字符",
                        Actual = info.Id.Length > 0 ? info.Id : "（空）",
                        Fix = "改成例如 <ID>" + SuggestId(info.Id) + "</ID>",
                        Line = LineOf(idElement),
                        Column = ColumnOf(idElement),
                    });
                }
            }

            // ── 其余字段按游戏行为读取（不校验，仅缺失时给警告） ──
            info.Name = ReadChild(root, "Name");
            info.Author = ReadChild(root, "Author");
            info.Version = ReadChild(root, "ModVersion");
            info.Description = ReadChild(root, "Description");
            info.GameVersions = ReadList(root, "GameVersions", "Version");
            info.Prerequisites = ReadList(root, "Prerequisites", "Mod");

            WarnMissingOptionalElements(result, root);

            // ── <Assemblies>：必填，至少一项，路径必须合法且存在 ──
            var listedPaths = new List<string>();
            XElement assembliesElement = root.Element("Assemblies");
            if (assembliesElement == null)
            {
                XElement near = ChildIgnoringCase(root, "Assemblies");
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "<Assemblies>",
                    Message = "找不到 <Assemblies> 元素，游戏不会加载任何代码并跳过这个模组",
                    Expect = "<Assemblies><Assembly>MyMod.dll</Assembly></Assemblies>（元素名区分大小写）",
                    Actual = near != null ? "找到的是 <" + near.Name.LocalName + ">" : "根节点下没有 <Assemblies>",
                    Fix = "补上 <Assemblies>，并把包内每个自定义 DLL 按加载顺序列进去",
                    Line = near != null ? LineOf(near) : rootLine,
                    Column = near != null ? ColumnOf(near) : ColumnOf(root),
                });
            }
            else
            {
                List<XElement> entries = assembliesElement.Elements("Assembly").ToList();
                if (entries.Count == 0)
                {
                    XElement near = assembliesElement.Elements().FirstOrDefault(
                        e => string.Equals(e.Name.LocalName, "Assembly", StringComparison.OrdinalIgnoreCase));
                    result.Issues.Add(new ManifestIssue
                    {
                        Level = ManifestIssueLevel.Error,
                        Element = "<Assemblies>/<Assembly>",
                        Message = entries.Count == 0 && near != null
                            ? "子元素写成了 <" + near.Name.LocalName + ">，游戏只认 <Assembly>"
                            : "<Assemblies> 里没有 <Assembly> 项，游戏不会加载任何代码",
                        Expect = "<Assembly>MyMod.dll</Assembly>",
                        Actual = near != null ? "<" + near.Name.LocalName + ">…</" + near.Name.LocalName + ">" : "空元素",
                        Fix = "把每个 DLL 写成一项 <Assembly>",
                        Line = LineOf(assembliesElement),
                        Column = ColumnOf(assembliesElement),
                    });
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement entry in entries)
                {
                    string raw = (entry.Value ?? "").Trim();
                    int line = LineOf(entry);
                    int column = ColumnOf(entry);
                    if (raw.Length == 0)
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Warning,
                            Element = "<Assembly>",
                            Message = "空的 <Assembly> 项",
                            Expect = "写相对包根的 .dll 路径，或直接删掉这一项",
                            Actual = "（空）",
                            Line = line,
                            Column = column,
                        });
                        continue;
                    }

                    string normalized = raw.Replace('\\', '/');
                    string problem = DescribeInvalidAssemblyPath(normalized);
                    if (problem != null)
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Error,
                            Element = "<Assembly>",
                            Message = problem,
                            Expect = "包内相对路径的 .dll，例如 MyMod.dll 或 lib/MyMod.dll",
                            Actual = raw,
                            Fix = "改成不越界、不是绝对路径、以 .dll 结尾的相对路径",
                            Line = line,
                            Column = column,
                        });
                        continue;
                    }

                    string full;
                    try
                    {
                        full = Path.GetFullPath(Path.Combine(folder, normalized));
                    }
                    catch (Exception)
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Error,
                            Element = "<Assembly>",
                            Message = "路径无法解析",
                            Expect = "包内相对路径的 .dll",
                            Actual = raw,
                            Fix = "去掉非法字符与控制字符",
                            Line = line,
                            Column = column,
                        });
                        continue;
                    }

                    string rootPath = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Error,
                            Element = "<Assembly>",
                            Message = "程序集指向模组文件夹外面，游戏会拒绝加载",
                            Expect = "包内相对路径",
                            Actual = raw + " → " + full,
                            Fix = "改成包内路径",
                            Line = line,
                            Column = column,
                        });
                        continue;
                    }

                    if (!seen.Add(full))
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Warning,
                            Element = "<Assembly>",
                            Message = "同一个程序集被列了多次，游戏只会加载一次",
                            Actual = raw,
                            Fix = "删掉重复项",
                            Line = line,
                            Column = column,
                        });
                        continue;
                    }
                    listedPaths.Add(full);

                    if (!File.Exists(full))
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Error,
                            Element = "<Assembly>",
                            Message = "清单列出的程序集在包里不存在",
                            Expect = "该文件随包一起发布",
                            Actual = raw + "（找不到 " + full + "）",
                            Fix = "把编译产物放进包内，或把这一项改成实际文件名（注意大小写）",
                            Line = line,
                            Column = column,
                        });
                    }
                }

                info.Assemblies = seen.Count == 0
                    ? new string[0]
                    : entries.Select(e => (e.Value ?? "").Trim().Replace('\\', '/'))
                             .Where(v => v.Length > 0)
                             .Where(v => DescribeInvalidAssemblyPath(v) == null)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToArray();

                // 游戏启用检查：至少一个列出的 DLL 含实现 IMod 的类型。
                List<string> existing = listedPaths.Where(File.Exists).ToList();
                if (existing.Count > 0 && !existing.Any(ContainsIModMarkup))
                {
                    result.Issues.Add(new ManifestIssue
                    {
                        Level = ManifestIssueLevel.Error,
                        Element = "<Assemblies>/<Assembly>",
                        Message = "列出的 DLL 里没有任何类型实现 IMod，游戏注册后也不会加载它",
                        Expect = "至少一个 DLL 实现 ProbablyStolen.IMod（并带 <Mod> 入口类）",
                        Actual = string.Join(", ", existing.Select(Path.GetFileName)),
                        Fix = "确认列的是 mod 本体 DLL，而不是依赖库；入口类要实现 IMod",
                        Line = LineOf(assembliesElement),
                        Column = ColumnOf(assembliesElement),
                    });
                }
            }

            // ── <Capabilities>：元素必填，未知能力名游戏拒绝加载 ──
            XElement capabilitiesElement = root.Element("Capabilities");
            if (capabilitiesElement == null)
            {
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "<Capabilities>",
                    Message = "找不到 <Capabilities> 元素，游戏会跳过这个模组",
                    Expect = "<Capabilities />（不需要额外能力时就写空元素）",
                    Actual = "根节点下没有 <Capabilities>",
                    Fix = "补上 <Capabilities />",
                    Line = rootLine,
                    Column = ColumnOf(root),
                });
            }
            else
            {
                info.Capabilities = capabilitiesElement.Elements("Capability")
                    .Select(e => (e.Value ?? "").Trim().ToLowerInvariant())
                    .Where(v => v.Length > 0)
                    .Distinct()
                    .ToArray();

                var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement cap in capabilitiesElement.Elements("Capability"))
                {
                    string value = (cap.Value ?? "").Trim();
                    string normalized = value.ToLowerInvariant();
                    if (normalized.Length == 0)
                    {
                        result.Issues.Add(new ManifestIssue
                        {
                            Level = ManifestIssueLevel.Warning,
                            Element = "<Capability>",
                            Message = "空的 <Capability> 项",
                            Actual = "（空）",
                            Fix = "删掉这一项",
                            Line = LineOf(cap),
                            Column = ColumnOf(cap),
                        });
                        continue;
                    }
                    if (KnownCapabilities.Contains(normalized)) continue;
                    if (!reported.Add(normalized)) continue;
                    result.Issues.Add(new ManifestIssue
                    {
                        Level = ManifestIssueLevel.Error,
                        Element = "<Capability>",
                        Message = "未知的能力名 '" + value + "'，游戏会跳过这个模组",
                        Expect = "只能是：" + string.Join(", ", KnownCapabilities) + "（不区分大小写）",
                        Actual = value,
                        Fix = "改正拼写；确实不需要时整项删掉。不要为了绕过扫描虚报能力",
                        Line = LineOf(cap),
                        Column = ColumnOf(cap),
                    });
                }
            }

            // ── 未列入清单的 DLL：游戏启用检查会拒绝（ui_mods_enable_unlisted） ──
            List<string> unlisted = FindUnlistedDlls(folder, listedPaths);
            if (unlisted.Count > 0)
            {
                XElement anchor = assembliesElement ?? root;
                result.Issues.Add(new ManifestIssue
                {
                    Level = ManifestIssueLevel.Error,
                    Element = "<Assemblies>",
                    Message = "包里有 " + unlisted.Count + " 个 DLL 没列进 <Assemblies>，游戏会注册这个模组但拒绝启用",
                    Expect = "包里每个 DLL 都要么列入 <Assemblies>，要么删掉",
                    Actual = string.Join(", ", unlisted),
                    Fix = "把这些 DLL 加到 <Assemblies>，或从包里删除（游戏/框架程序集不该随包发布）",
                    Line = LineOf(anchor),
                    Column = ColumnOf(anchor),
                });
            }
        }

        // ───────────────────────── 判据 ─────────────────────────

        /// <summary>与游戏 ModLoader.IsValidModId 完全一致；返回 null 表示合法。</summary>
        private static string DescribeInvalidId(string id)
        {
            if (id.Length == 0) return "<ID> 是空的，游戏会跳过这个模组";
            if (id.Length > 64) return "ID 有 " + id.Length + " 个字符，超过 64 的上限";
            if (id[0] == '.') return "ID 不能以 '.' 开头";
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok)
                    return "ID 第 " + (i + 1) + " 个字符 '" + c + "' 不允许，只允许字母、数字、_、-、.";
            }
            if (id == "." || id == "..") return "ID 不能是 \".\" 或 \"..\"";
            return null;
        }

        /// <summary>与游戏 ParseManifestCore 里对 assembly 路径的判断一致；返回 null 表示合法。</summary>
        private static string DescribeInvalidAssemblyPath(string normalized)
        {
            if (normalized.Contains("..")) return "程序集路径里不能出现 '..'";
            if (Path.IsPathRooted(normalized)) return "程序集路径必须是相对路径，不能是绝对路径";
            if (!normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return "程序集路径必须以 .dll 结尾";
            return null;
        }

        /// <summary>与游戏 AnyReferencesIMod 相同的启发式：字节串 "IMod\0" 是否存在。</summary>
        private static readonly byte[] IModBytes = { 0x49, 0x4D, 0x6F, 0x64, 0x00 };

        private static bool ContainsIModMarkup(string path)
        {
            try
            {
                return ContainsBytes(File.ReadAllBytes(path), IModBytes);
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0) return false;
            int last = haystack.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        /// <summary>与游戏 FolderDlls 一致：只扫包根与 Assemblies/ 这两个目录。</summary>
        private static List<string> FindUnlistedDlls(string folder, List<string> listedPaths)
        {
            var listed = new HashSet<string>(listedPaths, StringComparer.OrdinalIgnoreCase);
            string rootPath = Path.GetFullPath(folder).TrimEnd('\\', '/');
            var result = new List<string>();
            foreach (string dir in new[] { folder, Path.Combine(folder, "Assemblies") })
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir, "*.dll"); }
                catch { continue; }
                foreach (string file in files)
                {
                    string full = Path.GetFullPath(file);
                    if (listed.Contains(full)) continue;
                    result.Add(full.Substring(rootPath.Length).TrimStart('\\', '/').Replace('\\', '/'));
                }
            }
            return result;
        }

        // ───────────────────────── XML 辅助 ─────────────────────────

        private static ManifestIssue Error(string message, string expect, string actual, string fix)
        {
            return new ManifestIssue
            {
                Level = ManifestIssueLevel.Error,
                Message = message,
                Expect = expect,
                Actual = actual,
                Fix = fix,
            };
        }

        /// <summary>
        /// 游戏不校验这些元数据，缺了照样加载；但工坊页面会缺信息，所以合成一条警告而不是刷六条。
        /// </summary>
        private static void WarnMissingOptionalElements(ManifestLoadResult result, XElement root)
        {
            var missing = new List<string>();
            foreach (string name in new[] { "Name", "Author", "ModVersion", "Description" })
            {
                XElement element = root.Element(name);
                if (element == null || (element.Value ?? "").Trim().Length == 0) missing.Add("<" + name + ">");
            }
            foreach (string name in new[] { "GameVersions", "Prerequisites" })
            {
                if (root.Element(name) == null) missing.Add("<" + name + ">");
            }
            if (missing.Count == 0) return;

            result.Issues.Add(new ManifestIssue
            {
                Level = ManifestIssueLevel.Warning,
                Element = "<Manifest>",
                Message = "根节点下缺少 " + string.Join("、", missing) + "，游戏仍能加载",
                Expect = "Name/Author/ModVersion/Description 供游戏列表与工坊页面显示；" +
                         "GameVersions 写实际兼容版本（如 <GameVersions><Version>0.46D</Version></GameVersions>）；" +
                         "没有依赖时写 <Prerequisites />",
                Fix = "补上这些元素。它们不参与加载判据，缺了模组照样能用，但工坊页面会缺标题、作者、版本或描述",
                Line = LineOf(root),
                Column = ColumnOf(root),
            });
        }

        /// <summary>把 XmlException 的英文说明翻译成一句能直接看懂的中文。</summary>
        private static string SummarizeXmlError(string detail)
        {
            string lower = (detail ?? "").ToLowerInvariant();
            if (lower.Contains("unexpected end of file"))
            {
                string unclosed = UnclosedElementNames(detail);
                return unclosed.Length > 0
                    ? "有标签没有闭合，文件在标签结束前就结束了（未闭合：" + unclosed + "）"
                    : "有标签没有闭合，文件在标签结束前就结束了";
            }
            if (lower.Contains("does not match the end tag"))
            {
                string name = EndTagName(detail);
                return name.Length > 0
                    ? "开始标签和结束标签对不上（结束标签 </" + name + "> 没有对应的 <" + name + ">）"
                    : "开始标签和结束标签对不上";
            }
            if (lower.Contains("data at the root level is invalid") || lower.Contains("cannot be loaded as xml"))
                return "文件内容不是 XML（可能为空文件、纯文本，或编码不是 UTF-8/UTF-16）";
            if (lower.Contains("cannot begin with") || lower.Contains("invalid name") || lower.Contains("invalid token"))
                return "出现了非法字符，常见于标签名或属性名写错、少写 < > 或引号";
            if (lower.Contains("unexpected end tag")) return "多了一个结束标签";
            if (lower.Contains("whitespace") || lower.Contains("multiple root"))
                return "根节点之外还有内容，整份文件只能有一个根节点";
            return "XML 格式不对，细节见「实际」";
        }

        private static string UnclosedElementNames(string detail)
        {
            var match = Regex.Match(detail ?? "", @"not closed: ([^.]+)\.");
            if (!match.Success) return "";
            return string.Join("、", match.Groups[1].Value
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));
        }

        private static string EndTagName(string detail)
        {
            var match = Regex.Match(detail ?? "", @"does not match the end tag of '([^']+)'");
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>XmlException 的说明末尾自带 "Line N, position M."，位置已经单独显示，去掉避免重复。</summary>
        private static string TrimLineSuffix(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var match = Regex.Match(message, @"\s*Line \d+, position \d+\.\s*$");
            return match.Success ? message.Substring(0, match.Index).TrimEnd() : message.Trim();
        }

        private static string ReadChild(XElement root, string name)
        {
            return (root.Element(name)?.Value ?? "").Trim();
        }

        private static string[] ReadList(XElement root, string container, string item)
        {
            XElement element = root.Element(container);
            if (element == null) return new string[0];
            return element.Elements(item)
                .Select(e => (e.Value ?? "").Trim())
                .Where(v => v.Length > 0)
                .ToArray();
        }

        private static XElement ChildIgnoringCase(XElement parent, string name)
        {
            return parent.Elements().FirstOrDefault(
                e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(e.Name.LocalName, name, StringComparison.Ordinal));
        }

        private static int LineOf(XElement element)
        {
            try
            {
                var info = (IXmlLineInfo)element;
                return info.HasLineInfo() ? info.LineNumber : 0;
            }
            catch { return 0; }
        }

        private static int ColumnOf(XElement element)
        {
            try
            {
                var info = (IXmlLineInfo)element;
                return info.HasLineInfo() ? info.LinePosition : 0;
            }
            catch { return 0; }
        }

        private static string SuggestId(string invalid)
        {
            var sb = new StringBuilder();
            foreach (char c in invalid)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                sb.Append(ok ? c : '_');
            }
            string id = sb.ToString().Trim('.');
            if (id.Length == 0) return "my_mod";
            return id.Length > 64 ? id.Substring(0, 64) : id;
        }

        /// <summary>工坊描述：优先用 manifest 的 Description，纯英文时追加中文提示由调用方决定。</summary>
        public string BuildDescription()
        {
            return Description;
        }
    }
}
