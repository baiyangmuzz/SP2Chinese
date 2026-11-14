using System;
using System.IO;
using SP2Chinese.Setup;

class Real {
    static void Main() {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string localVer = "1.6.2";
        long localBuild = 202611011750;

        var v = Updater.CheckVersionAsync(localVer).GetAwaiter().GetResult();
        Console.WriteLine("插件检查: " + v.Message);
        Console.WriteLine("  线上=" + v.ModVersion + " 在线=" + v.Online);

        var d = Updater.CheckAsync(localBuild, "b4343617ea10").GetAwaiter().GetResult();
        Console.WriteLine("词库检查: " + d.Message);
        Console.WriteLine("  HasUpdate=" + d.HasUpdate + " build=" + d.Build);
        if (d.Dictionary != null) Console.WriteLine("  下载大小=" + d.Dictionary.Length);
    }
}
