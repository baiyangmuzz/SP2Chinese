using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SP2Chinese;

internal static class Program
{
    private static int Main(string[] args)
    {
        var dict = args.Length > 0 ? args[0] : throw new ArgumentException("dict path required");
        var probe = args.Length > 1 ? args[1] : null;
        var log = Path.Combine(Path.GetTempPath(), "sp2zh_miss.txt");
        if (File.Exists(log)) File.Delete(log);

        _benchDict = dict;
        var t = new Translator(dict, log);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("entries: " + t.Count);

        if (probe == "--manifest")
        {
            var json = File.ReadAllText(args[2]);
            var m = MiniJson.Parse(json);
            Console.WriteLine("parse: " + (m != null ? "OK" : "FAILED"));
            if (m != null)
            {
                object v;
                Console.WriteLine("  modVersion  = " + (m.TryGet("modVersion", out v) ? v : "<none>"));
                Console.WriteLine("  dictVersion = " + (m.TryGet("dictVersion", out v) ? v : "<none>"));
                Console.WriteLine("  dictRows    = " + (m.TryGet("dictRows", out v) ? v : "<none>"));
                Console.WriteLine("  author      = " + (m.TryGet("author", out v) ? v : "<none>"));
                if (m.TryGet("update", out v) && v is MiniJson up && up.TryGet("dictionary", out var u))
                    Console.WriteLine("  dict url    = " + u);
            }
            return 0;
        }

        if (probe == "--diag")
        {
            Diag(t);
            return 0;
        }

        if (probe == "--bench")
        {
            Bench(t);
            return 0;
        }

        if (probe == null)
        {
            // sanity: dictionary round trip and placeholder audit
            var bad = 0;
            foreach (var line in File.ReadAllLines(dict))
            {
                var i = line.IndexOf('\t');
                if (i <= 0) continue;
                var key = Translator.Unescape(line.Substring(0, i));
                var val = Translator.Unescape(line.Substring(i + 1));
                var k = Args(key);
                var v = Args(val);
                if (k.Count != v.Count)
                {
                    if (bad < 40) Console.WriteLine("PLACEHOLDER MISMATCH: [" + key.Replace("\n", "\\n") + "] -> [" + val.Replace("\n", "\\n") + "]  " + string.Join(",", k) + " vs " + string.Join(",", v));
                    bad++;
                }
                if (t.Apply(key) != val && t.Apply(key) != key)
                {
                    if (bad < 40) Console.WriteLine("ROUNDTRIP FAIL: [" + key.Replace("\n", "\\n") + "] => [" + t.Apply(key).Replace("\n", "\\n") + "]");
                    bad++;
                }
            }
            Console.WriteLine("problems: " + bad);
            return 0;
        }

        var lines = File.ReadAllLines(probe);
        var hit = 0;
        foreach (var raw in lines)
        {
            var text = raw.Replace("\\r", "\r").Replace("\\n", "\n");
            var res = t.Apply(text);
            var ok = res != text;
            if (ok) hit++;
            Console.WriteLine((ok ? "HIT  " : "MISS ") + Quote(raw) + (ok ? "  =>  " + Quote(res.Replace("\n", "\\n")) : ""));
        }
        Console.WriteLine("hit " + hit + "/" + lines.Length);
        return 0;
    }

    private static List<string> Args(string s)
    {
        var res = new List<string>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;
            var j = s.IndexOf('}', i);
            if (j < 0) continue;
            var inner = s.Substring(i + 1, j - i - 1);
            var colon = inner.IndexOf(':');
            var name = colon >= 0 ? inner.Substring(0, colon) : inner;
            res.Add(name);
            i = j;
        }
        return res;
    }

    private static string Quote(string s)
    {
        return s.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    /// <summary>
    /// Cold pass = first sighting of each string (regex pipeline).
    /// Hot pass = the steady state the game actually runs in once memoization is in place.
    /// </summary>
    /// <summary>Dumps the private lookup tables so a silent indexing bug is visible.</summary>
    private static void Diag(Translator t)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (var name in new[] { "_segmentIndex", "_shortcutIndex" })
        {
            var f = typeof(Translator).GetField(name, flags);
            if (f == null) { Console.WriteLine(name + ": FIELD MISSING"); continue; }
            var dict = f.GetValue(t) as System.Collections.IDictionary;
            if (dict == null) { Console.WriteLine(name + ": null"); continue; }
            Console.WriteLine(name + ": " + dict.Count + " bucket(s)");
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                var list = e.Value as System.Collections.IEnumerable;
                Console.WriteLine("   '" + e.Key + "' bucket:");
                if (list == null) continue;
                foreach (var item in list)
                {
                    var tp = item.GetType();
                    var pre = tp.GetField("Prefix", flags);
                    var suf = tp.GetField("Suffix", flags);
                    var parts = tp.GetField("Parts", flags);
                    if (pre != null)
                        Console.WriteLine("      shortcut prefix=[" + pre.GetValue(item) + "] suffix=[" + suf.GetValue(item) + "]");
                    else if (parts != null)
                        Console.WriteLine("      segment parts=[" + string.Join(" || ", (string[])parts.GetValue(item)) + "]");
                }
            }
        }

        var probes = new[] { "Loaded 'Kicking Fish'", "Part damaged: Fuselage", "白杨木 tried to defy gravity and lost" };
        foreach (var p in probes)
            Console.WriteLine("APPLY [" + p + "] => [" + t.Apply(p) + "]");

        var ts = typeof(Translator).GetMethod("TrySegment", flags);
        var tc = typeof(Translator).GetMethod("TryShortcut", flags);
        if (ts != null)
            foreach (var p in probes)
                Console.WriteLine("  TrySegment[" + p + "] => [" + (ts.Invoke(t, new object[] { p }) ?? "<null>") + "]");
        if (tc != null)
            foreach (var p in probes)
                Console.WriteLine("  TryShortcut[" + p + "] => [" + (tc.Invoke(t, new object[] { p }) ?? "<null>") + "]");
    }

    private static void Bench(Translator t)
    {
        var staticText = new List<string>();
        foreach (var line in File.ReadAllLines(_benchDict))
        {
            var i = line.IndexOf('\t');
            if (i <= 0) continue;
            var v = line.Substring(i + 1).TrimEnd('\r', '\n');
            if (v.Length > 0 && v != line.Substring(0, i) && staticText.Count < 60)
                staticText.Add(line.Substring(0, i));
        }
        var dynamicText = new List<string>();
        for (var i = 0; i < 20; i++)
            dynamicText.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "THR{0}", i * 137 % 100));
        dynamicText.Add("Wind Speed: 12mph");
        dynamicText.Add("Fuel: 87.5%");
        var missText = new List<string> { "zzz-untranslated-a", "zzz-untranslated-b" };

        var samples = new List<string>();
        samples.AddRange(staticText);
        samples.AddRange(dynamicText);
        samples.AddRange(missText);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var s in samples) t.Apply(s);
        sw.Stop();
        Console.WriteLine("cold  : {0} strings in {1} ms  ({2:F1} us/string)",
            samples.Count, sw.Elapsed.TotalMilliseconds,
            sw.Elapsed.TotalMilliseconds * 1000 / samples.Count);

        const int rounds = 200;
        sw.Restart();
        for (var r = 0; r < rounds; r++)
            foreach (var s in samples) t.Apply(s);
        sw.Stop();
        var total = (long)samples.Count * rounds;
        Console.WriteLine("hot   : {0} calls in {1} ms  ({2:F3} us/string)",
            total, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds * 1000 / total);
        Console.WriteLine("cache : hits={0} misses={1}", t.CacheHits, t.CacheMisses);
    }

    private static string _benchDict;
}