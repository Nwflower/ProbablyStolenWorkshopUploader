using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProbablyStolenWorkshopUploader
{
    /// <summary>从 steamcmd 的 workshopitem VDF 导入常用发布字段，不执行上传。</summary>
    public sealed class WorkshopVdf
    {
        public uint AppId;
        public ulong PublishedFileId;
        public string ContentFolder;
        public string PreviewFile;
        public int? Visibility;
        public string Title;
        public string Description;
        public string ChangeNote;
        public readonly List<string> Tags = new List<string>();
        public readonly List<string> UnsupportedFields = new List<string>();

        public static WorkshopVdf Parse(string text)
        {
            var reader = new Reader(text ?? "");
            Token root = reader.Next();
            if (root.Kind != TokenKind.Text || !string.Equals(root.Value, "workshopitem", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("VDF 根节点应为 workshopitem。");
            if (reader.Next().Kind != TokenKind.Open)
                throw new InvalidDataException("workshopitem 后缺少左花括号。");

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tags = new List<string>();
            while (true)
            {
                Token key = reader.Next();
                if (key.Kind == TokenKind.Close) break;
                if (key.Kind != TokenKind.Text)
                    throw new InvalidDataException("VDF 字段名无效或缺少右花括号。");
                Token value = reader.Next();

                if (value.Kind == TokenKind.Open)
                {
                    // 只支持 tags 的嵌套写法：{ "0" "Mod" "1" "Gameplay" }
                    if (!string.Equals(key.Value, "tags", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("VDF 字段包含不支持的嵌套对象：" + key.Value);
                    while (true)
                    {
                        Token innerKey = reader.Next();
                        if (innerKey.Kind == TokenKind.Close) break;
                        if (innerKey.Kind != TokenKind.Text)
                            throw new InvalidDataException("VDF 的 tags 段格式无效。");
                        Token innerValue = reader.Next();
                        if (innerValue.Kind != TokenKind.Text)
                            throw new InvalidDataException("VDF 的 tags 值格式无效。");
                        AddTag(tags, innerValue.Value);
                    }
                    continue;
                }

                if (value.Kind != TokenKind.Text)
                    throw new InvalidDataException("VDF 字段缺少值或包含不支持的嵌套对象：" + key.Value);

                if (string.Equals(key.Value, "tags", StringComparison.OrdinalIgnoreCase))
                {
                    // 也接受单行写法："tags" "Mod,Gameplay"
                    foreach (string part in (value.Value ?? "").Split(new[] { ',', '，', ';' }))
                        AddTag(tags, part);
                    continue;
                }

                if (!values.TryAdd(key.Value, value.Value))
                    throw new InvalidDataException("VDF 字段重复：" + key.Value);
            }
            if (reader.Next().Kind != TokenKind.End)
                throw new InvalidDataException("workshopitem 后有多余内容。");

            if (!values.TryGetValue("appid", out string appId) ||
                !uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsedAppId))
                throw new InvalidDataException("VDF 缺少有效的 appid。");
            if (!values.TryGetValue("publishedfileid", out string id) ||
                !ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsedId))
                throw new InvalidDataException("VDF 缺少有效的 publishedfileid；新建物品请填 0。");
            if (!values.TryGetValue("contentfolder", out string folder) || string.IsNullOrWhiteSpace(folder))
                throw new InvalidDataException("VDF 缺少 contentfolder。");

            var result = new WorkshopVdf
            {
                AppId = parsedAppId,
                PublishedFileId = parsedId,
                ContentFolder = folder,
                PreviewFile = Get(values, "previewfile"),
                Title = Get(values, "title"),
                Description = Get(values, "description"),
                ChangeNote = Get(values, "changenote"),
            };
            result.Tags.AddRange(tags);
            if (values.TryGetValue("visibility", out string visibility))
            {
                if (!int.TryParse(visibility, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 0 || v > 3)
                    throw new InvalidDataException("VDF 的 visibility 必须是 0、1、2 或 3。");
                result.Visibility = v;
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "appid", "publishedfileid", "contentfolder", "previewfile",
                "visibility", "title", "description", "changenote", "tags"
            };
            foreach (string key in values.Keys)
                if (!known.Contains(key)) result.UnsupportedFields.Add(key);
            return result;
        }

        private static string Get(Dictionary<string, string> values, string key) =>
            values.TryGetValue(key, out string value) ? value : null;

        /// <summary>去空格、忽略大小写去重后追加一个 TAG。</summary>
        private static void AddTag(List<string> tags, string value)
        {
            string tag = (value ?? "").Trim();
            if (tag.Length == 0) return;
            foreach (string existing in tags)
                if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) return;
            tags.Add(tag);
        }

        private enum TokenKind { End, Text, Open, Close }

        private struct Token
        {
            public TokenKind Kind;
            public string Value;
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _index;

            public Reader(string text) { _text = text; }

            public Token Next()
            {
                SkipSpace();
                if (_index >= _text.Length) return new Token { Kind = TokenKind.End };
                char c = _text[_index++];
                if (c == '{') return new Token { Kind = TokenKind.Open };
                if (c == '}') return new Token { Kind = TokenKind.Close };
                if (c != '"')
                {
                    int start = _index - 1;
                    while (_index < _text.Length && !char.IsWhiteSpace(_text[_index]) &&
                           _text[_index] != '{' && _text[_index] != '}') _index++;
                    return new Token { Kind = TokenKind.Text, Value = _text.Substring(start, _index - start) };
                }

                var value = new StringBuilder();
                while (_index < _text.Length)
                {
                    c = _text[_index++];
                    if (c == '"') return new Token { Kind = TokenKind.Text, Value = value.ToString() };
                    if (c != '\\' || _index >= _text.Length)
                    {
                        value.Append(c);
                        continue;
                    }
                    char next = _text[_index++];
                    switch (next)
                    {
                        case '\\': value.Append('\\'); break;
                        case '"': value.Append('"'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        default: value.Append('\\').Append(next); break;
                    }
                }
                throw new InvalidDataException("VDF 的引号未闭合。");
            }

            private void SkipSpace()
            {
                while (_index < _text.Length)
                {
                    if (char.IsWhiteSpace(_text[_index]) || _text[_index] == '\uFEFF') { _index++; continue; }
                    if (_text[_index] == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                    {
                        _index += 2;
                        while (_index < _text.Length && _text[_index] != '\n') _index++;
                        continue;
                    }
                    break;
                }
            }
        }
    }
}
