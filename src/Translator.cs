using System;
using System.Collections.Generic;
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

        public void Load(string path)
        {
            var map = new Dictionary<string, string>();
            var used = new HashSet<char>();
            _shortcutIndex.Clear();
            _segmentIndex.Clear();
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

            _usedChars.Length = 0;
            foreach (var c in used) _usedChars.Append(c);
        }

        private static int FixedLength(SegmentEntry e)
        {
            var n = 0;
            for (var i = 0; i < e.Parts.Length; i++) n += e.Parts[i].Length;
            return n;
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

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

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

            var parts = Unwrap(text);
            var prefix = parts.Item1;
            var core = parts.Item2;
            var suffix = parts.Item3;

            if (core != text && TryDirect(core, out direct))
                return string.Concat(prefix, direct, suffix);

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
                    formatted = string.Format(template, masked.Item2);
                }
                catch
                {
                    formatted = null;
                }

                if (formatted != null)
                    return string.Concat(prefix, formatted, suffix);
            }

            // Second pass: the rendered string has the key binding already substituted in
            // ("Shortcut: Ctrl + S"), so the bracket form stored in the dictionary will never
            // match by equality. Match on the text around the binding and reuse the captured
            // key combo in the translation.
            var viaShortcut = TryShortcut(stripped);
            if (viaShortcut != null) return string.Concat(prefix, viaShortcut, suffix);

            // Third pass: concatenated messages with one or more embedded values, matched on the
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
            // needs to stop one pathological scene from writing forever. 30000 keeps a full session
            // of browsing (settings panels alone can miss thousands) without unbounded growth.
            // the credits screen alone burns through it before any real UI text is reached.
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
