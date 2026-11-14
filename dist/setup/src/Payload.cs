using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace SP2Chinese.Setup
{
    /// <summary>
    /// 内嵌 payload 的读取与校验。
    ///
    /// 全程在内存里做完：从 exe 资源读出 zip -> 校验每条目的 SHA256 -> 直接写进游戏目录。
    /// 不再「先解压到临时目录再让脚本拷一遍」，少了两次磁盘往返，
    /// 也不会有临时目录残留需要清理。
    /// </summary>
    internal sealed class Payload
    {
        private const string ResourceName = "SP2Chinese.Setup.payload.zip";

        internal sealed class Entry
        {
            public string Path;      // zip 内相对路径，统一用 / 分隔
            public long Length;
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private Payload() { }

        /// <summary>扫描内嵌 zip 的条目表（不读内容，很快）。</summary>
        internal static Payload Open()
        {
            return OpenCore(() => OpenResource());
        }

        /// <summary>从外部 zip 文件打开（离线测试与调试用）。</summary>
        internal static Payload OpenFromFile(string zipPath)
        {
            return OpenCore(() => File.OpenRead(zipPath));
        }

        private static Payload OpenCore(Func<Stream> opener)
        {
            var p = new Payload();
            using (Stream s = opener())
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (name.EndsWith("/")) continue;
                    p._entries[name] = new Entry { Path = name, Length = e.Length };
                }
            }
            p._opener = opener;
            return p;
        }

        private Func<Stream> _opener;

        internal bool Contains(string relativePath)
        {
            return _entries.ContainsKey(Normalize(relativePath));
        }

        /// <summary>按需读取单个条目到内存。文件都很小（最大 795 KB），整体常驻也无压力。</summary>
        internal byte[] Read(string relativePath)
        {
            string key = Normalize(relativePath);
            Func<Stream> opener = _opener ?? (Func<Stream>)OpenResource;
            using (Stream s = opener())
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                ZipArchiveEntry e = zip.GetEntry(key);
                if (e == null) throw new FileNotFoundException("payload 缺少文件：" + key);
                using (Stream es = e.Open())
                using (var ms = new MemoryStream((int)e.Length))
                {
                    es.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>写出到目标路径。先写 .tmp 再替换，避免写一半断电留下破损文件。</summary>
        internal void WriteTo(string relativePath, string destPath)
        {
            byte[] data = Read(relativePath);
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = destPath + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(destPath))
            {
                try { File.SetAttributes(destPath, FileAttributes.Normal); } catch { }
                File.Delete(destPath);
            }
            File.Move(tmp, destPath);
        }

        private static string Normalize(string p)
        {
            return (p ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static Stream OpenResource()
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) throw new InvalidOperationException("安装包不完整：缺少内嵌 payload");
            return s;
        }

        // ────────────────────────────────────────────────────────────
        // 哈希
        // ────────────────────────────────────────────────────────────

        internal static string Sha256OfBytes(byte[] data)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(data));
        }

        internal static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ToHex(sha.ComputeHash(fs));
        }

        private static string ToHex(byte[] hash)
        {
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
