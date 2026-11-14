using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// manifest.json 的只读视图：版本号、字典哈希、以及每个待写文件的 SHA256。
    ///
    /// 这份清单是安装校验的唯一可信锚点 —— 它和 exe 一起分发，不经过网络。
    /// 任何时候都不允许「用被复制的源文件自己算哈希给自己验」，那等于没校验。
    ///
    /// 用自带的 MiniJson 解析而不是 System.Web.Extensions：后者在 WPF 的 XAML 编译期
    /// 会引发程序集解析失败（MC1000: Could not find assembly 'System.Web'）。
    /// </summary>
    internal sealed class Manifest
    {
        internal string ModVersion = "";
        // 安装器自身的信息（可选）。API 被墙时靠它拿到可校验的下载地址：
        // manifest 走 raw.githubusercontent / jsDelivr，国内可达。
        internal string SetupVersion = "";
        internal string SetupSha256 = "";
        internal string SetupUrl = "";
        internal long SetupSize;
        internal string DictVersion = "";
        internal long DictBuild;
        internal long DictRows;
        internal string DictSha256 = "";
        internal string UpdateDictionary = "";

        internal readonly Dictionary<string, FileHash> Files =
            new Dictionary<string, FileHash>(StringComparer.OrdinalIgnoreCase);

        internal sealed class FileHash
        {
            public string Sha256 = "";
            public long Size;
        }

        internal static Manifest Load(string jsonPath)
        {
            return Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
        }

        internal static Manifest Parse(string json)
        {
            MiniJson root = MiniJson.Parse(json);
            if (root == null) throw new FormatException("manifest 解析失败");

            var m = new Manifest
            {
                ModVersion = Str(root, "modVersion"),
                DictVersion = Str(root, "dictVersion"),
                DictBuild = Long(root, "dictBuild"),
                DictRows = Long(root, "dictRows"),
                DictSha256 = Str(root, "dictSha256")
            };

            MiniJson upd = Sub(root, "update");
            if (upd != null) m.UpdateDictionary = Str(upd, "dictionary");

            // setup：安装器自身的版本 / 哈希 / 下载地址。
            // 有 sha256 才允许自更新（无哈希的下载一律只提示、不执行）。
            MiniJson setup = Sub(root, "setup");
            if (setup != null)
            {
                m.SetupVersion = Str(setup, "version");
                m.SetupSha256 = Str(setup, "sha256");
                m.SetupUrl = Str(setup, "url");
                m.SetupSize = Long(setup, "size");
            }

            MiniJson files = Sub(root, "files");
            if (files != null)
            {
                foreach (var kv in files.Items)
                {
                    var meta = kv.Value as MiniJson;
                    if (meta == null) continue;
                    m.Files[kv.Key] = new FileHash
                    {
                        Sha256 = Str(meta, "sha256"),
                        Size = Long(meta, "size")
                    };
                }
            }
            return m;
        }

        private static MiniJson Sub(MiniJson node, string key)
        {
            if (node == null) return null;
            object v;
            return node.TryGet(key, out v) ? v as MiniJson : null;
        }

        private static string Str(MiniJson node, string key)
        {
            if (node == null) return "";
            object v;
            if (!node.TryGet(key, out v) || v == null) return "";
            var s = v as string;
            if (s != null) return s;
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is bool) return (bool)v ? "true" : "false";
            return "";
        }

        private static long Long(MiniJson node, string key)
        {
            return long.TryParse(Str(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) ? r : 0;
        }
    }

    /// <summary>极简 JSON 读取器，只为解析 manifest。支持对象/数组/字符串/数字/布尔/null。</summary>
    internal sealed class MiniJson
    {
        private readonly Dictionary<string, object> _map =
            new Dictionary<string, object>(StringComparer.Ordinal);

        internal IEnumerable<KeyValuePair<string, object>> Items { get { return _map; } }

        internal bool TryGet(string key, out object value) { return _map.TryGetValue(key, out value); }

        internal int Count { get { return _map.Count; } }

        internal object this[string key]
        {
            get { object v; return _map.TryGetValue(key, out v) ? v : null; }
        }

        internal void Set(string key, object value) { _map[key] = value; }

        internal static MiniJson Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i, 0);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("JSON 尾部有多余内容 @" + i);
            return v as MiniJson;
        }

        private const int MaxDepth = 32;

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("JSON 嵌套过深");
            SkipWs(s, ref i);
            if (i >= s.Length) return null;

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i, depth + 1);
                case '[': return ParseArray(s, ref i, depth + 1);
                case '"': return ParseString(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static void Expect(string s, ref int i, string lit)
        {
            if (i + lit.Length > s.Length || string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0)
                throw new FormatException("无效的 JSON 字面量 @" + i);
            i += lit.Length;
        }

        private static MiniJson ParseObject(string s, ref int i, int depth)
        {
            var obj = new MiniJson();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }

            while (i < s.Length)
            {
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("缺少 ':' @" + i);
                i++;
                object val = ParseValue(s, ref i, depth);
                if (key != null) obj._map[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                throw new FormatException("缺少 ',' 或 '}' @" + i);
            }
            return obj;
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            var list = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (i < s.Length)
            {
                list.Add(ParseValue(s, ref i, depth));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                throw new FormatException("缺少 ',' 或 ']' @" + i);
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            string hex = s.Substring(i, 4);
                            int code;
                            if (int.TryParse(hex, NumberStyles.HexNumber, null, out code)) sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            string token = s.Substring(start, i - start);
            if (token.Length == 0) throw new FormatException("缺少数值 @" + start);
            long l;
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("无效数值 '" + token + "'");
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
