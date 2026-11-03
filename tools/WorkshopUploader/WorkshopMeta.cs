using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace ProbablyStolenWorkshopUploader
{
    /// <summary>某个语言的工坊标题与描述。</summary>
    [DataContract]
    public sealed class LangText
    {
        [DataMember(Order = 1)] public string Title = "";
        [DataMember(Order = 2)] public string Desc = "";
    }

    /// <summary>按 mod ID 保存的发布信息：物品 ID、可见性、预览图、各语言文案。</summary>
    [DataContract]
    public sealed class ModMeta
    {
        [DataMember(Order = 1)] public ulong PublishedId;
        [DataMember(Order = 2)] public int Visibility = 2; // 0 公开 / 1 仅好友 / 2 隐藏 / 3 未列出
        [DataMember(Order = 3)] public string PreviewPath = "";
        [DataMember(Order = 4)] public Dictionary<string, LangText> Languages = new Dictionary<string, LangText>();
        [DataMember(Order = 5)] public bool PendingFirstPublish;
        // 本地记住上次填写的工坊 TAG，作为查询不到现有 TAG 时的回退预填。
        [DataMember(Order = 6, EmitDefaultValue = false)] public List<string> Tags = new List<string>();

        public LangText For(string steamLang)
        {
            if (Languages == null) Languages = new Dictionary<string, LangText>();
            if (!Languages.TryGetValue(steamLang, out LangText t) || t == null)
            {
                t = new LangText();
                Languages[steamLang] = t;
            }
            return t;
        }
    }

    /// <summary>
    /// 本地记忆：%APPDATA%\ProbablyStolenWorkshopUploader\workshop-meta.json。
    /// 放在用户目录而不是 mod 文件夹，避免污染要上传的内容。
    /// </summary>
    public sealed class MetaStore
    {
        [DataContract]
        private sealed class Root
        {
            [DataMember(Order = 1)] public Dictionary<string, ModMeta> Mods = new Dictionary<string, ModMeta>();
            [DataMember(Order = 2, EmitDefaultValue = false)] public bool CommunityNoticeSeen;
        }

        private readonly string _path;
        private Root _root = new Root();

        public MetaStore(string directory = null)
        {
            string dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProbablyStolenWorkshopUploader");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "workshop-meta.json");
            Load();
        }

        public string FilePath => _path;
        public bool CommunityNoticeSeen => _root.CommunityNoticeSeen;

        public bool RememberCommunityNotice()
        {
            if (_root.CommunityNoticeSeen) return true;
            _root.CommunityNoticeSeen = true;
            if (Save()) return true;
            _root.CommunityNoticeSeen = false;
            return false;
        }

        public ModMeta Get(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return new ModMeta();
            if (!_root.Mods.TryGetValue(modId, out ModMeta m) || m == null)
            {
                m = new ModMeta();
                _root.Mods[modId] = m;
            }
            if (m.Languages == null) m.Languages = new Dictionary<string, LangText>();
            return m;
        }

        public bool RememberPublishedId(string modId, ulong id, bool? pendingFirstPublish = null)
        {
            if (string.IsNullOrEmpty(modId) || id == 0) return false;
            ModMeta meta = Get(modId);
            if (meta.PublishedId != id) meta.PendingFirstPublish = false;
            meta.PublishedId = id;
            if (pendingFirstPublish.HasValue) meta.PendingFirstPublish = pendingFirstPublish.Value;
            return Save();
        }

        public bool Save()
        {
            string tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var ser = new DataContractJsonSerializer(typeof(Root));
                    ser.WriteObject(fs, _root);
                }
                File.Move(tempPath, _path, true);
                LastError = "";
                return true;
            }
            catch
            {
                // 记忆文件写失败不该阻断上传，界面会在日志里提示。
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                LastError = "写入本地记忆失败：" + _path;
                return false;
            }
        }

        public string LastError = "";

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                using (var fs = File.OpenRead(_path))
                {
                    var ser = new DataContractJsonSerializer(typeof(Root));
                    _root = (Root)ser.ReadObject(fs) ?? new Root();
                }
            }
            catch
            {
                _root = new Root(); // 文件损坏时从空记忆开始，不影响上传
            }
        }
    }
}
