using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SP2Chinese
{
    public class Translator
    {
        public static Translator I { get; private set; }

        private Dictionary<string, string> _map = new Dictionary<string, string>();
        private readonly HashSet<string> _logged = new HashSet<string>();
        private int _logCount;
        private readonly string _logPath;
        private readonly StringBuilder _usedChars = new StringBuilder();

        // Memoization: Apply is a pure function of its input, and the hot paths (HUD values
        // re-set every frame, the scene-load sweep over every TMP object) feed it the same few
        // hundred strings over and over. Without the cache every miss walks three regexes.
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private long _cacheHits;
        private long _cacheMisses;

        private static readonly Regex Tag = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex Number = new Regex("-?\\d+(?:\\.\\d+)?", RegexOptions.Compiled);
        private static readonly Regex NameOf = new Regex("^<\\s*/?\\s*([a-zA-Z0-9]+)");

        // "[SaveAircraft]" is a binding reference, not display text: the game swaps it for the
        // real key combo ("Ctrl + S") before the string reaches the screen. So the rendered
        // text contains no brackets at all and a dictionary row storing the bracket form can
        // never match by equality - which used to make all 85 shortcut tooltips silently
        // untranslatable. Match on the fixed prefix/suffix around the binding instead.
        private static readonly Regex Shortcut = new Regex(@"\[[A-Za-z][A-Za-z0-9]*\]", RegexOptions.Compiled);
        private readonly Dictionary<char, List<ShortcutEntry>> _shortcutIndex =
            new Dictionary<char, List<ShortcutEntry>>();

        // Messages the game builds by concatenation ("Mirroring enabled for part " + name + " and
        // automatically created mirrored part " + name) can't be matched by equality and contain
        // more than one variable. A dictionary row may mark those holes with {*} and the same
        // marker is reused in the translation:
        //     key  : Mirroring enabled for part {*} and automatically created mirrored part {*}
        //     value: 已为部件 {*} 开启镜像，并自动创建了镜像部件 {*}
        private const string Wildcard = "{*}";
        private readonly Dictionary<char, List<SegmentEntry>> _segmentIndex =
            new Dictionary<char, List<SegmentEntry>>();

        // Many game strings are produced from C# format templates such as
        // " pitch angle between {2:F0} and {3:F0} degrees." By the time TMP sees the text the
        // placeholders are gone, so MaskNumbers' strict "{0},{1},..." keys can never match them:
        // index numbers that do not start at 0, format/alignment specifiers, and literal digits
        // in the fixed text all break that path. Instead of forcing contributors to rewrite 300+
        // rows (and losing the format information), those rows are indexed as anchored regex
        // patterns: each placeholder becomes a capture group for the already-rendered value and
        // the captured text is substituted back into the translation, matched by slot id so a
        // translation may reorder its placeholders without breaking.
        private static readonly Regex FormatToken = new Regex(
            @"\{(?:\d+(?:,-?\d+)?(?::[^}]*)?|[A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
        private readonly Dictionary<char, List<FormatEntry>> _formatIndex =
            new Dictionary<char, List<FormatEntry>>();

        private sealed class FormatEntry
        {
            internal Regex Pattern;
            internal string[] ValueParts;   // literal text around the translation's placeholders
            internal int[] ValueGroups;     // capture group index for each gap (0 = drop)
            internal int FixedLength;       // total literal length in the key; longer = more specific
            internal int AnchorChars;       // non-whitespace literal chars; guards over-matching
        }

        private sealed class ShortcutEntry
        {
            internal string Prefix;
            internal string Suffix;
            internal string Value;
            internal string Slot;
        }

        private sealed class SegmentEntry
        {
            internal string[] Parts;    // fixed text between the wildcards; length = holes + 1
            internal string Value;
        }
        private static readonly HashSet<string> SelfClose = new HashSet<string>
        {
            "br", "page", "sprite", "nobr", "mspace", "pos"
        };

        // Dynamic text (fuel gauges, coordinates) mints a new key every frame, so the cache
        // must not grow without bound. A hard reset is crude but O(1) and self-healing: the
        // static UI strings that matter are re-cached within one sweep after a reset.
        private const int CacheLimit = 8192;

        // Rows whose key has significant leading/trailing whitespace (craft Instructions always
        // do). Indexed under the trimmed form so a blank-line difference at either end still hits.
        private readonly Dictionary<string, string> _trimmedIndex = new Dictionary<string, string>();


        public Translator(string dictPath, string logPath)
        {
            _logPath = logPath;
            Load(dictPath);
            I = this;
        }

        public int Count
        {
            get { return _map.Count; }
        }

        public long CacheHits
        {
            get { return _cacheHits; }
        }

        public long CacheMisses
        {
            get { return _cacheMisses; }
        }

        /// <summary>Every character that appears in a translated value, used to populate the font atlas.</summary>
        public string UsedCharacters
        {
            get { return _usedChars.ToString(); }
        }

        /// <summary>Number of distinct format-template rows indexed for the regex fallback path.</summary>
        public int FormatTemplateCount
        {
            get
            {
                var seen = new HashSet<FormatEntry>();
                foreach (var kv in _formatIndex)
                    foreach (var e in kv.Value) seen.Add(e);
                return seen.Count;
            }
        }

        public void Load(string path)
        {
            var map = new Dictionary<string, string>();
            var used = new HashSet<char>();
            _shortcutIndex.Clear();
            _segmentIndex.Clear();
            _formatIndex.Clear();
            _trimmedIndex.Clear();
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var i = line.IndexOf('\t');
                    if (i <= 0) continue;
                    var key = Unescape(line.Substring(0, i));
                    var val = Unescape(line.Substring(i + 1));
                    if (val.Length == 0) continue;
                    // "保留" 是历史批次里表示「不翻译、保留英文原文」的标记值，不是译文。
                    // 把它归一成恒等行，避免运行时把文本翻成「保留」二字并丢掉 {0} 占位符。
                    if (val == "\u4fdd\u7559") val = key;

                    map[key] = val;

                    // Craft Instructions carry significant leading/trailing blank lines in the
                    // XML; the game may hand us a different amount. Keep a trimmed alias.
                    var tk = key.Trim();
                    if (tk.Length > 0 && tk.Length != key.Length && !_trimmedIndex.ContainsKey(tk))
                        _trimmedIndex[tk] = val;

                    // Index rows whose key carries exactly one [Binding], keyed by the first
                    // character of the fixed prefix so a lookup only scans a handful of rows.
                    //
                    // The "Shortcut:" guard is load-bearing: the dictionary is full of log rows
                    // shaped like "[Error] A critical error occurred...", where the bracket is a
                    // severity prefix, not a key binding. Indexing those as "empty prefix + long
                    // suffix" made TryShortcut swallow arbitrary UI strings on its empty-prefix
                    // pass, which silently blocked the {*} templates that ran after it.
                    var sm = key.IndexOf("Shortcut", StringComparison.OrdinalIgnoreCase) >= 0
                        ? Shortcut.Match(key)
                        : Match.Empty;
                    if (sm.Success)
                    {
                        var tail = key.Substring(sm.Index + sm.Length);
                        if (!Shortcut.IsMatch(tail))
                        {
                            var entry = new ShortcutEntry
                            {
                                Prefix = key.Substring(0, sm.Index),
                                Suffix = tail,
                                Value = val,
                                Slot = sm.Value
                            };
                            var bucket = BucketOf(entry.Prefix);
                            List<ShortcutEntry> list;
                            if (!_shortcutIndex.TryGetValue(bucket, out list))
                            {
                                list = new List<ShortcutEntry>();
                                _shortcutIndex[bucket] = list;
                            }
                            list.Add(entry);
                        }
                    }

                    // Format-template rows: keys that MaskNumbers can never reproduce (format
                    // specifiers, non-zero indices, literal digits in fixed text, named tokens).
                    IndexFormatRow(key, val);

                    // Segment templates: fixed text separated by {*} holes.
                    if (key.IndexOf(Wildcard, StringComparison.Ordinal) >= 0)
                    {
                        var segs = key.Split(new[] { Wildcard }, StringSplitOptions.None);
                        if (segs.Length >= 2)
                        {
                            var seg = new SegmentEntry { Parts = segs, Value = val };
                            var segBucket = BucketOf(segs[0]);
                            List<SegmentEntry> segList;
                            if (!_segmentIndex.TryGetValue(segBucket, out segList))
                            {
                                segList = new List<SegmentEntry>();
                                _segmentIndex[segBucket] = segList;
                            }
                            segList.Add(seg);
                        }
                    }

                    foreach (var c in val)
                        if (c > 127) used.Add(c);
                }
            }
            _map = map;
            _cache.Clear();
            // 热重载后允许同一字符串重新记录：旧字典没翻、新字典仍没翻的文本
            // 应该出现在新的 untranslated.txt 里，否则会被旧集合永久屏蔽。
            _logged.Clear();
            _logCount = 0;

            // Longer fixed text = more specific template. Without this ordering,
            // "Mirroring enabled for part {*}" swallows every multi-part variant that starts
            // with the same words, leaving the {*} hole to eat the whole tail of the message.
            foreach (var kv in _segmentIndex)
                kv.Value.Sort(delegate (SegmentEntry a, SegmentEntry b)
                {
                    return FixedLength(b).CompareTo(FixedLength(a));
                });
            foreach (var kv in _shortcutIndex)
                kv.Value.Sort(delegate (ShortcutEntry a, ShortcutEntry b)
                {
                    return (b.Prefix.Length + b.Suffix.Length)
                        .CompareTo(a.Prefix.Length + a.Suffix.Length);
                });
            foreach (var kv in _formatIndex)
                kv.Value.Sort(delegate (FormatEntry a, FormatEntry b)
                {
                    // Anchor characters first: a row with more literal text is strictly more
                    // specific, and this keeps "  {2}"-style weak rows from winning.
                    var byAnchor = b.AnchorChars.CompareTo(a.AnchorChars);
                    if (byAnchor != 0) return byAnchor;
                    return b.FixedLength.CompareTo(a.FixedLength);
                });

            _usedChars.Length = 0;
            foreach (var c in used) _usedChars.Append(c);
        }

        private static int FixedLength(SegmentEntry e)
        {
            var n = 0;
            for (var i = 0; i < e.Parts.Length; i++) n += e.Parts[i].Length;
            return n;
        }

        // Value rendered by a C# format placeholder. It is not always a number: the game also
        // formats player names, enum names ("Vector2m"), dates and free text through the same
        // placeholders. A lazy "anything but newline" capture keeps those rows matchable; the
        // pattern stays anchored at both ends and is ordered by literal length, so the literal
        // text around the holes still decides which template wins. Surrounding spaces are part of
        // the capture so ",9"-style alignment padding is absorbed rather than leaked.
        private const string RenderedValue = @"[^\r\n]*?";

        /// <summary>Strips the ,alignment / :format part from a placeholder body ("2:F0" -> "2").</summary>
        private static string SlotId(string token)
        {
            var id = token.Substring(1, token.Length - 2);
            var colon = id.IndexOf(':');
            var comma = id.IndexOf(',');
            var cut = colon >= 0 ? colon : (comma >= 0 ? comma : id.Length);
            return id.Substring(0, cut);
        }

        /// <summary>
        /// Compiles a dictionary row whose key is a C# format template into an anchored regex.
        /// Only rows that MaskNumbers can NOT reproduce are indexed here: format/alignment
        /// specifiers, indices that do not start at 0, literal digits in the fixed text, or named
        /// placeholders. Each key placeholder becomes one capture group; the translation is split
        /// on the same token syntax and each gap is mapped to the group of the matching slot, so
        /// the translation may reorder its placeholders without breaking.
        /// </summary>
        private void IndexFormatRow(string key, string val)
        {
            var keyMatches = FormatToken.Matches(key);
            if (keyMatches.Count == 0) return;
            if (key == val) return;   // identity rows are deliberately not translated

            var fixedText = FormatToken.Replace(key, "");
            var plain = fixedText.IndexOfAny(new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' }) < 0;
            for (var i = 0; i < keyMatches.Count && plain; i++)
                if (keyMatches[i].Value != "{" + i.ToString(CultureInfo.InvariantCulture) + "}")
                    plain = false;
            if (plain) return;   // MaskNumbers already handles this row

            // slot id -> 1-based ordinal among distinct slots. Each occurrence consumes its own
            // rendered value, so the Nth capture group belongs to the Nth slot in first-seen
            // order; the translation's placeholder with the same slot id reuses that group.
            var slotGroup = new Dictionary<string, int>(StringComparer.Ordinal);
            var pattern = new StringBuilder("^");
            var pos = 0;
            foreach (Match m in keyMatches)
            {
                pattern.Append(Regex.Escape(key.Substring(pos, m.Index - pos)));
                var slot = SlotId(m.Value);
                if (!slotGroup.ContainsKey(slot)) slotGroup[slot] = slotGroup.Count + 1;
                pattern.Append('(').Append(RenderedValue).Append(')');
                pos = m.Index + m.Length;
            }
            pattern.Append(Regex.Escape(key.Substring(pos))).Append('$');

            Regex rx;
            try
            {
                rx = new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                return;   // malformed pattern: leave the row to the exact/shortcut paths
            }

            var valueParts = new List<string>();
            var valueGroups = new List<int>();
            var vp = 0;
            foreach (Match m in FormatToken.Matches(val))
            {
                valueParts.Add(val.Substring(vp, m.Index - vp));
                int g;
                valueGroups.Add(slotGroup.TryGetValue(SlotId(m.Value), out g) ? g : 0);
                vp = m.Index + m.Length;
            }
            valueParts.Add(val.Substring(vp));

            // Bucket by what the *rendered* string actually starts with. A key beginning with a
            // placeholder ("{0:n1}k damage") renders with the value first, so it goes to the
            // wildcard bucket; otherwise the first character of the key is a reliable index.
            // Keeping the wildcard bucket small matters: every miss scans its whole list.
            // A row whose fixed text carries fewer than two non-whitespace characters is too weak
            // an anchor: "  {2}" would match any string and swallow its spaces, and "{0:n0}d "
            // would claim unrelated text ending in "d ". Such rows carry no real translation
            // (their value is the key with whitespace trimmed) so they are left out entirely.
            var anchorChars = 0;
            for (var c = 0; c < fixedText.Length; c++)
                if (!char.IsWhiteSpace(fixedText[c])) anchorChars++;
            if (anchorChars < 2) return;

            var entry = new FormatEntry
            {
                Pattern = rx,
                ValueParts = valueParts.ToArray(),
                ValueGroups = valueGroups.ToArray(),
                FixedLength = fixedText.Length,
                AnchorChars = anchorChars
            };
            var leadingHole = key.StartsWith("{", StringComparison.Ordinal);
            if (leadingHole) AddFormatToBucket('\0', entry);
            var ownBucket = BucketOf(key);
            if (!leadingHole && ownBucket != '\0') AddFormatToBucket(ownBucket, entry);
        }

        private void AddFormatToBucket(char bucket, FormatEntry entry)
        {
            List<FormatEntry> list;
            if (!_formatIndex.TryGetValue(bucket, out list))
            {
                list = new List<FormatEntry>();
                _formatIndex[bucket] = list;
            }
            list.Add(entry);
        }

        /// <summary>Tries the compiled format templates, longest fixed text first.</summary>
        private string TryFormat(string s)
        {
            var own = BucketOf(s);
            for (var pass = 0; pass < 2; pass++)
            {
                var key = pass == 0 ? own : '\0';
                if (pass == 1 && key == own) break;

                List<FormatEntry> list;
                if (!_formatIndex.TryGetValue(key, out list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    // A single literal character ("{0:n0}d ") is not enough of an anchor: it would
                    // claim unrelated strings that happen to end in "d ". Require two non-space
                    // literal characters before this row is allowed to translate anything.
                    if (e.AnchorChars < 2) continue;
                    var m = e.Pattern.Match(s);
                    if (!m.Success) continue;
                    var sb = new StringBuilder();
                    for (var p = 0; p < e.ValueGroups.Length; p++)
                    {
                        sb.Append(e.ValueParts[p]);
                        var g = e.ValueGroups[p];
                        if (g > 0 && g < m.Groups.Count) sb.Append(m.Groups[g].Value);
                    }
                    sb.Append(e.ValueParts[e.ValueParts.Length - 1]);
                    return sb.ToString();
                }
            }
            return null;
        }

        /// <summary>Expands the two-character escapes \n \r \t \\ used inside the TSV file.</summary>
        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                switch (s[i + 1])
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 游戏原文（源语言）是英文/ASCII。一个已含 CJK 字符的字符串必然是：
        /// 我们刚写回 TMP 组件的译文、玩家输入的中文、或服务器下发的中文。
        /// 这些都不该再走一遍匹配流水线，更不该被记进 untranslated.txt —— 否则
        /// 每次 Scan/Awake 回写都会把译文当成「漏译」刷进日志，淹没真实缺口。
        /// </summary>
        private static bool ContainsCjk(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c >= '\u2e80' && c <= '\u9fff') return true;
                if (c >= '\uf900' && c <= '\ufaff') return true;
                if (c >= '\uff00' && c <= '\uffef') return true;
                if (c >= '\u3000' && c <= '\u303f') return true;
            }
            return false;
        }

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 已译中文（含回写触发的二次 Apply）：直接返回，不缓存也不记 Miss。
            // 不缓存是为了让字典里若真有中文 key（当前为 0 条）时仍可命中；
            // 这类字符串数量不稳定，缓存收益低而污染风险高。
            if (ContainsCjk(text) && !_map.ContainsKey(text)) return text;

            string cached;
            if (_cache.TryGetValue(text, out cached))
            {
                _cacheHits++;
                return cached;
            }

            _cacheMisses++;
            var result = ApplyUncached(text);

            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[text] = result;
            return result;
        }

        private string ApplyUncached(string text)
        {
            string direct;
            if (TryDirect(text, out direct)) return direct;

            // 恒等行（key == value）是「刻意保留英文」的标记，不是漏译。
            // 旧实现让它们跑完整套正则流水线并最终 Miss()，把 1708 条英文原文
            // 刷进 untranslated.txt。这里直接短路，既不记日志也不再尝试模糊匹配。
            string identity;
            if (_map.TryGetValue(text, out identity) && identity == text) return text;

            var parts = Unwrap(text);
            var prefix = parts.Item1;
            var core = parts.Item2;
            var suffix = parts.Item3;

            if (core != text && TryDirect(core, out direct))
                return string.Concat(prefix, direct, suffix);
            if (core != text)
            {
                string coreIdentity;
                if (_map.TryGetValue(core, out coreIdentity) && coreIdentity == core)
                    return text;   // 保留原样（含标签），不记日志
            }

            var stripped = Tag.Replace(core, "");

            // Craft Instructions are stored with significant leading/trailing whitespace in the
            // XML and the game may hand us a slightly different amount than the file holds.
            // Retry with the trimmed string so a one-blank-line difference doesn't lose the
            // whole 400-character paragraph.
            var trimmed = stripped.Trim();
            if (trimmed.Length > 0 && trimmed.Length != stripped.Length)
            {
                string trimmedHit;
                if (_trimmedIndex.TryGetValue(trimmed, out trimmedHit))
                    return string.Concat(prefix, trimmedHit, suffix);
            }

            var masked = MaskNumbers(stripped);
            if (masked.Item1 != stripped &&
                _map.TryGetValue(masked.Item1, out var template) &&
                template != masked.Item1)
            {
                string formatted = null;
                try
                {
                    // 捕获的是运行时文本里的数字「字符串」。若直接传给 string.Format，
                    // {0:n1} / {0,8:P1} / {1:F0} 这类格式说明符会被忽略（对齐仍生效），
                    // 精度丢失。尽量还原成数值，失败再退回字符串。
                    var args = new object[masked.Item2.Length];
                    for (var a = 0; a < masked.Item2.Length; a++)
                    {
                        var raw = masked.Item2[a];
                        decimal dec;
                        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out dec))
                            args[a] = dec;
                        else
                            args[a] = raw;
                    }
                    formatted = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                              template, args);
                }
                catch
                {
                    // 模板里的日期/自定义格式对数值也可能不适用，最后一次尝试用原始字符串。
                    try { formatted = string.Format(template, masked.Item2); }
                    catch { formatted = null; }
                }

                if (formatted != null)
                    return string.Concat(prefix, formatted, suffix);
            }

            // Second pass: C# format templates whose rendered numbers MaskNumbers cannot
            // reproduce verbatim (format specifiers, non-zero indices, literal digits in the
            // fixed text, named placeholders). Runs after the fast path so plain {0} rows win.
            var viaFormat = TryFormat(stripped);
            if (viaFormat != null) return string.Concat(prefix, viaFormat, suffix);

            // Third pass: the rendered string has the key binding already substituted in
            // ("Shortcut: Ctrl + S"), so the bracket form stored in the dictionary will never
            // match by equality. Match on the text around the binding and reuse the captured
            // key combo in the translation.
            var viaShortcut = TryShortcut(stripped);
            if (viaShortcut != null) return string.Concat(prefix, viaShortcut, suffix);

            // Fourth pass: concatenated messages with one or more embedded values, matched on the
            // fixed text around the {*} holes.
            var viaSegment = TrySegment(stripped);
            if (viaSegment != null) return string.Concat(prefix, viaSegment, suffix);

            Miss(text);
            return text;
        }

        /// <summary>
        /// Matches a shortcut tooltip. Two lookups: the bucket for the string's first character,
        /// then the dedicated empty-prefix bucket (rows whose key starts with the binding itself).
        /// </summary>
        private string TryShortcut(string s)
        {
            var own = BucketOf(s);
            for (var pass = 0; pass < 2; pass++)
            {
                var key = pass == 0 ? own : '\0';
                if (pass == 1 && key == own) break;

                List<ShortcutEntry> list;
                if (!_shortcutIndex.TryGetValue(key, out list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    var inner = s.Length - e.Prefix.Length - e.Suffix.Length;
                    if (inner <= 0) continue;
                    if (!s.StartsWith(e.Prefix, StringComparison.Ordinal)) continue;
                    if (!s.EndsWith(e.Suffix, StringComparison.Ordinal)) continue;
                    var actual = s.Substring(e.Prefix.Length, inner);
                    return e.Value.Replace(e.Slot, actual);
                }
            }
            return null;
        }

        /// <summary>
        /// Matches a concatenated message against a {*} template. Captured values are translated
        /// as well when they are short enough to be a part or player name, so
        /// "Part damaged: Fuselage" becomes "部件受损：机身" rather than keeping the English.
        /// </summary>
        private string TrySegment(string s)
        {
            var own = BucketOf(s);
            for (var pass = 0; pass < 2; pass++)
            {
                var key = pass == 0 ? own : '\0';
                if (pass == 1 && key == own) break;

                List<SegmentEntry> list;
                if (!_segmentIndex.TryGetValue(key, out list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    var segs = e.Parts;
                    if (!s.StartsWith(segs[0], StringComparison.Ordinal)) continue;

                    var pos = segs[0].Length;
                    var caps = new List<string>(segs.Length - 1);
                    var matched = true;

                    // Middle sections are located by search; the final section must sit flush
                    // against the end, otherwise the match is a coincidence.
                    for (var p = 1; p < segs.Length - 1; p++)
                    {
                        var at = s.IndexOf(segs[p], pos, StringComparison.Ordinal);
                        if (at < 0) { matched = false; break; }
                        caps.Add(s.Substring(pos, at - pos));
                        pos = at + segs[p].Length;
                    }
                    if (!matched) continue;

                    var tail = segs[segs.Length - 1];
                    if (!s.EndsWith(tail, StringComparison.Ordinal)) continue;
                    var tailStart = s.Length - tail.Length;
                    if (tailStart < pos) continue;
                    caps.Add(s.Substring(pos, tailStart - pos));

                    var localized = e.Value;
                    for (var c = 0; c < caps.Count; c++)
                    {
                        var piece = caps[c];
                        if (piece.Length > 0 && piece.Length <= 80 && piece.IndexOf('\n') < 0)
                        {
                            var translated = Apply(piece);
                            if (translated != piece) piece = translated;
                        }
                        localized = ReplaceFirst(localized, Wildcard, piece);
                    }
                    return localized;
                }
            }
            return null;
        }

        private static char BucketOf(string s)
        {
            return string.IsNullOrEmpty(s) ? '\0' : char.ToLowerInvariant(s[0]);
        }

        /// <summary>Replaces the first occurrence of <paramref name="token"/>, left to right.</summary>
        private static string ReplaceFirst(string s, string token, string value)
        {
            var i = s.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return s;
            return s.Substring(0, i) + value + s.Substring(i + token.Length);
        }

        private bool TryDirect(string key, out string value)
        {
            if (_map.TryGetValue(key, out value) && value != key) return true;
            value = null;
            return false;
        }

        private static (string, string, string) Unwrap(string s)
        {
            var prefix = "";
            var suffix = "";
            while (s.Length > 0 && s[0] == '<')
            {
                var gt = s.IndexOf('>');
                if (gt < 0) break;
                var tag = s.Substring(0, gt + 1);
                var m = NameOf.Match(tag);
                if (!m.Success) break;
                if (tag.Contains("</")) break;
                if (tag.EndsWith("/>")) break;
                var name = m.Groups[1].Value;
                if (SelfClose.Contains(name)) break;
                var close = "</" + name + ">";
                if (!s.EndsWith(close)) break;
                prefix += tag;
                suffix = close + suffix;
                s = s.Substring(tag.Length, s.Length - tag.Length - close.Length);
            }
            return (prefix, s, suffix);
        }

        private static (string, string[]) MaskNumbers(string s)
        {
            var digits = new List<string>();
            var masked = Number.Replace(s, m =>
            {
                digits.Add(m.Value);
                return "{" + (digits.Count - 1).ToString() + "}";
            });
            return (masked, digits.ToArray());
        }

        private void Miss(string text)
        {
            // A clean log is written every launch (Plugin.Awake deletes it), so the cap only
            // needs to stop one pathological scene from writing forever. 20000 keeps a full
            // session of browsing (settings panels alone can miss thousands) without
            // unbounded growth.
            if (_logCount >= 20000) return;
            if (!_logged.Add(text)) return;
            _logCount++;
            try
            {
                File.AppendAllText(_logPath, text.Replace("\r", "\\r").Replace("\n", "\\n") + "\n");
            }
            catch
            {
                // ignore logging failures
            }
        }
    }
}
