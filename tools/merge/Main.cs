using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class Program
{
    private static readonly Regex ArgIndex = new Regex(@"\{(\d+)(?:[^}]*)\}");

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // merge <baseDict> <corpusFiles...> @@ <additionFiles...> [--prefer-existing] [--force] [--write=<path>]
        var basePath = args[0];
        var flags = args.Where(a => a.StartsWith("--")).ToArray();
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();
        var preferExisting = flags.Contains("--prefer-existing");
        var forceMode = flags.Contains("--force");
        var writeTarget = flags.FirstOrDefault(f => f.StartsWith("--write="))?.Substring("--write=".Length);

        var sep = Array.IndexOf(positional, "@@");
        if (sep < 0) return Fail("missing -- separator");
        var corpus = positional.Skip(1).Take(sep - 1).ToArray();
        var additions = positional.Skip(sep + 1).ToArray();

        var corpusSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in corpus)
        {
            if (!File.Exists(f)) { Console.WriteLine("corpus missing: " + f); continue; }
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
            {
                var i = line.IndexOf('\t');
                var raw = i >= 0 ? line.Substring(0, i) : line;
                corpusSet.Add(raw.Replace("\\r", "\r").Replace("\\n", "\n"));
            }
        }
        Console.WriteLine("corpus strings: " + corpusSet.Count);

        var order = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var bad = 0;

        foreach (var line in File.ReadAllLines(basePath, Encoding.UTF8))
        {
            var i = line.IndexOf('\t');
            if (i <= 0) { Console.WriteLine("SKIP malformed base line: " + Trunc(line)); continue; }
            order.Add(line);
            map[line.Substring(0, i)] = line.Substring(i + 1);
        }
        Console.WriteLine("base entries: " + order.Count);

        foreach (var f in additions)
        {
            if (!File.Exists(f)) { Console.WriteLine("addition missing: " + f); bad++; continue; }
            foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                var i = line.IndexOf('\t');
                if (i <= 0)
                {
                    Console.WriteLine("MALFORMED (" + Path.GetFileName(f) + "): [" + line + "]");
                    bad++;
                    continue;
                }

                var key = line.Substring(0, i);
                var val = line.Substring(i + 1);
                var unescKey = Unescape(key);
                var unescVal = Unescape(val);

                if (!corpusSet.Contains(unescKey))
                {
                    Console.WriteLine("NOT IN CORPUS (" + Path.GetFileName(f) + "): [" + key + "]");
                    bad++;
                }

                var ka = Args(unescKey);
                var va = Args(unescVal);
                if (!ka.OrderBy(x => x).SequenceEqual(va.OrderBy(x => x)))
                {
                    Console.WriteLine("PLACEHOLDER MISMATCH: [" + key + "] -> [" + val + "]  " +
                        string.Join(",", ka) + " vs " + string.Join(",", va));
                    bad++;
                }

                if (key == val)
                {
                    Console.WriteLine("IDENTITY (no-op): [" + key + "]");
                }

                if (map.TryGetValue(key, out var old))
                {
                    if (old == val) continue;
                    if (preferExisting && !forceMode)
                    {
                        Console.WriteLine("KEEP EXISTING: [" + key + "]  [" + val + "]");
                        continue;
                    }
                    Console.WriteLine("OVERRIDE: [" + key + "]  [" + old + "] -> [" + val + "]");
                }
                else
                {
                    order.Add(line);
                }

                map[key] = val;
            }
        }

        Console.WriteLine("problems: " + bad);
        if (writeTarget != null)
        {
            var sb = new StringBuilder();
            foreach (var l in order) sb.Append(l).Append('\n');
            File.WriteAllText(writeTarget, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("wrote " + order.Count + " entries -> " + writeTarget);
        }

        return bad == 0 ? 0 : 1;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 2;
    }

    private static string Trunc(string s)
    {
        return s.Length <= 90 ? s : s.Substring(0, 90) + "...";
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
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

    private static List<string> Args(string s)
    {
        var res = new List<string>();
        foreach (Match m in ArgIndex.Matches(s)) res.Add(m.Groups[1].Value);
        return res;
    }
}