using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SP2Chinese
{
    [BepInPlugin("sp2.zh", "SimplePlanes 2 \u7b80\u4f53\u4e2d\u6587 by \u767d\u6768\u6728", "1.6.1")]
    public class Plugin : BaseUnityPlugin
    {
        private string _dictPath;
        private string _logPath;
        private string _buildPath;
        private DateTime _mtime;
        private int _frame;
        private float _updateDelay = 15f;      // 启动后等 15 秒再联网，别和加载抢带宽
        private string _pendingHandled;
        private ConfigEntry<bool> _autoUpdate;

        private void Awake()
        {
            var dir = Path.GetDirectoryName(Info.Location);
            _dictPath = Path.Combine(dir, "dictionary_zh_cn.tsv");
            _logPath = Path.Combine(dir, "untranslated.txt");
            _buildPath = Path.Combine(dir, "loaded-build.txt");
            // The miss log used to be appended across sessions, so it accumulated strings that
            // had been translated months earlier and buried the real gaps. Start each run clean.
            try { if (File.Exists(_logPath)) File.Delete(_logPath); }
            catch { /* read-only install dir; keep appending instead */ }
            var translator = new Translator(_dictPath, _logPath);

            _autoUpdate = Config.Bind("更新", "自动检查词库更新", true,
                "启动 15 秒后检查 GitHub 是否发布了新词库。只下载词典（数据文件），不会下载或替换程序本体。");
            MachineIdentifiers.Load();
            _mtime = File.GetLastWriteTime(_dictPath);

            // A single bad patch target used to abort the whole chain: v1.4.3 shipped a patch
            // pointing at TMPro.TMP_Text.Awake, which does not exist, and the mod loaded fine
            // while every hook stayed unapplied - the UI came up fully English. Attaching the
            // classes one by one means a future mistake costs one feature, not the whole mod.
            var patches = PatchAllSafe();

            // Written after patching so the file also records whether the hooks really attached.
            try
            {
                File.WriteAllText(_buildPath,
                    Info.Metadata.Version + "\t" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    "\tidentifiers=" + MachineIdentifiers.Count +
                    "\tpatches=" + patches + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { /* never let diagnostics break startup */ }

            FontLoader.MissingPath = Path.Combine(dir, "font_missing.txt");
            FontLoader.Setup(translator.UsedCharacters);
            Logger.LogInfo(FontLoader.Assets.Count > 0
                ? "font ready: " + FontLoader.Assets.Count + " atlas(es), " + FontLoader.TotalGlyphs + " glyphs"
                : "font fallback missing");

            SceneManager.sceneLoaded += (s, m) => Scan();
            Logger.LogInfo("SP2 chinese " + Info.Metadata.Version + " by \u767d\u6768\u6728 | QQ\u7fa4 1128992783 | " +
                "github.com/baiyangmuzz/SP2Chinese | " + MachineIdentifiers.Count + " identifiers, patches " + patches);
        }

        /// <summary>Applies every HarmonyPatch class in this assembly independently.</summary>
        private string PatchAllSafe()
        {
            var harmony = new Harmony("sp2.zh");
            var applied = 0;
            var failed = new List<string>();

            Type[] types;
            try { types = typeof(Plugin).Assembly.GetTypes(); }
            catch (Exception e) { Logger.LogError("cannot enumerate types: " + e.Message); return "0/0"; }

            foreach (var type in types)
            {
                if (type == null || !type.IsClass) continue;
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception e)
                {
                    failed.Add(type.Name);
                    Logger.LogWarning("[SP2Chinese] patch " + type.Name + " skipped: " + e.Message);
                }
            }

            return applied + "/" + (applied + failed.Count) +
                   (failed.Count > 0 ? " (failed: " + string.Join(",", failed.ToArray()) + ")" : "");
        }

        private void Update()
        {
            _frame++;
            if (_frame % 60 != 0) return;

            // Belt and braces: the Awake hooks cover instantiated prefabs, but if one ever
            // fails to attach (or a component slips past them) this sweep still gets the text
            // translated within five seconds instead of never. Cheap now that Apply memoizes.
            if (_frame % 300 == 0) Scan();

            // ---- 在线词库更新 ----
            if (_autoUpdate != null && _autoUpdate.Value)
            {
                if (_updateDelay > 0f)
                {
                    _updateDelay -= 1f;   // 每秒一次调用，够用
                    if (_updateDelay <= 0f)
                        DictionaryUpdater.Kick(_dictPath, Info.Metadata.Version.ToString());
                }

                var pending = DictionaryUpdater.PendingPath;
                if (pending != null && pending != _pendingHandled)
                {
                    _pendingHandled = pending;
                    ApplyDownloadedDictionary(pending);
                }
            }

            // Only the dictionary hot-reload check lives here now.
            var now = File.GetLastWriteTime(_dictPath);
            if (now != _mtime)
            {
                _mtime = now;
                Translator.I.Load(_dictPath);
                // Without this the new values render as tofu boxes until the next restart:
                // the atlas is only built in Awake, and hot reload used to skip it.
                FontLoader.Ensure(Translator.I.UsedCharacters);
                Scan();
            }
        }

        /// <summary>
        /// Full-scene fallback, deliberately rare now: once per scene load and once per
        /// dictionary hot-reload. The Awake hooks handle everything instantiated afterwards,
        /// so nothing needs to be polled on a timer anymore.
        /// </summary>
        /// <summary>把下载好的字典换上去并热重载，旧文件留一份 .bak-auto。</summary>
        private void ApplyDownloadedDictionary(string downloaded)
        {
            try
            {
                if (File.Exists(_dictPath)) File.Copy(_dictPath, _dictPath + ".bak-auto", true);
                File.Copy(downloaded, _dictPath, true);
                try { File.Delete(downloaded); } catch { }

                Translator.I.Load(_dictPath);
                FontLoader.Ensure(Translator.I.UsedCharacters);
                _mtime = File.GetLastWriteTime(_dictPath);
                Scan();

                Logger.LogInfo("dictionary auto-updated: " + Translator.I.Count + " entries");
            }
            catch (Exception e)
            {
                Logger.LogWarning("dictionary auto-update failed: " + e.Message);
            }
        }

        private void Scan()
        {
            var tmps = Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>();
            foreach (var t in tmps)
            {
                if (!t.gameObject.scene.IsValid()) continue;
                if (MachineText.Skip(t.text, t)) continue;
                var s = Translator.I.Apply(t.text);
                if (s != t.text) t.text = s;
            }

            var uis = Resources.FindObjectsOfTypeAll<Text>();
            foreach (var t in uis)
            {
                if (!t.gameObject.scene.IsValid()) continue;
                if (MachineText.Skip(t.text, t)) continue;
                var s = Translator.I.Apply(t.text);
                if (s != t.text) t.text = s;
            }
        }
    }

    /// <summary>
    /// Builds TMP font assets that can actually render the translated text.
    ///
    /// A single 2048x2048 atlas only holds roughly a thousand CJK glyphs, which is far fewer
    /// than the ~1600 characters the dictionary needs (and much fewer than the ~3800 needed to
    /// also cover player/chat text). Everything that does not fit comes out as a tofu box, so
    /// the atlas is created large and pre-populated, spilling into extra atlases when needed.
    /// </summary>
    public static class FontLoader
    {
        public static readonly List<TMPro.TMP_FontAsset> Assets = new List<TMPro.TMP_FontAsset>();
        public static int TotalGlyphs;
        public static string MissingPath;

        private const int SamplingPointSize = 45;
        private const int AtlasPadding = 4;
        private const int AtlasSize = 4096;
        private const int BatchSize = 128;

        private static readonly string[] Files =
        {
            @"C:\Windows\Fonts\msyhbd.ttc",
            @"C:\Windows\Fonts\msyh.ttc",
            @"C:\Windows\Fonts\simhei.ttf",
            @"C:\Windows\Fonts\Deng.ttf",
            @"C:\Windows\Fonts\msyhl.ttc",
            @"C:\Windows\Fonts\simkai.ttf",
            @"C:\Windows\Fonts\STSONG.TTF"
        };

        public static void Setup(string usedCharacters)
        {
            Assets.Clear();
            TotalGlyphs = 0;

            var wanted = new SortedSet<int>();
            for (var c = 0x20; c <= 0x7E; c++) wanted.Add(c);
            foreach (var c in CommonChars.Text) wanted.Add(c);
            foreach (var c in usedCharacters ?? string.Empty) wanted.Add(c);
            for (var c = 0x3000; c <= 0x303F; c++) wanted.Add(c);
            for (var c = 0xFF01; c <= 0xFF60; c++) wanted.Add(c);
            foreach (var c in "\u00b7\u00ae\u00b0\u2014\u2018\u2019\u201c\u201d\u2026\u2192\u25b2\u25bc\u00d7\u00f7")
                wanted.Add(c);

            var pending = new List<int>(wanted);
            var total = pending.Count;
            var unresolved = new List<int>();
            var path = PickFont();
            var perAtlas = 0;

            for (var pass = 0; pass < 5 && pending.Count > 0; pass++)
            {
                // First atlas is large enough for the whole translation; any spillover atlas is
                // smaller so a surprise overflow cannot balloon VRAM usage.
                var size = pass == 0 ? AtlasSize : AtlasSize / 2;
                var asset = Make(path, size);
                if (asset == null) break;

                var leftover = Populate(asset, pending);
                var fitted = pending.Count - leftover.Count;
                Assets.Add(asset);
                TotalGlyphs += fitted;
                if (perAtlas == 0) perAtlas = fitted;

                UnityEngine.Debug.Log("[SP2Chinese] atlas " + (pass + 1) + " (" + size + "px): " + fitted +
                                      " glyphs, " + leftover.Count + " left");

                if (fitted == 0 || leftover.Count == pending.Count)
                {
                    unresolved = leftover;
                    break;
                }

                pending = leftover;
            }

            if (pending.Count > 0 && unresolved.Count == 0) unresolved = pending;
            if (unresolved.Count > 0 && perAtlas > 0)
                UnityEngine.Debug.LogWarning("[SP2Chinese] one atlas holds about " + perAtlas +
                                             " glyphs but " + total + " were requested");

            Register();

            if (unresolved.Count > 0)
            {
                var sb = new StringBuilder();
                sb.Append("# ").Append(unresolved.Count).Append(" characters did not fit into the font atlas.")
                    .AppendLine();
                foreach (var c in unresolved) sb.Append(c).Append('\t').AppendLine(((char)c).ToString());
                try
                {
                    if (MissingPath != null) File.WriteAllText(MissingPath, sb.ToString(), new UTF8Encoding(false));
                }
                catch
                {
                    // ignore
                }
            }
            else if (MissingPath != null && File.Exists(MissingPath))
            {
                try { File.Delete(MissingPath); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Adds characters that a hot-reloaded dictionary introduced into the atlases that
        /// already exist. Rebuilding from scratch is the last resort, because every live
        /// TMP_Text still points at the old assets and a rebuild makes the whole UI swap fonts.
        /// </summary>
        public static void Ensure(string usedCharacters)
        {
            if (Assets.Count == 0)
            {
                Setup(usedCharacters);
                return;
            }

            var seen = new HashSet<int>();
            var pending = new List<int>();
            foreach (var c in usedCharacters ?? string.Empty)
                if (c > 127 && seen.Add(c)) pending.Add(c);
            if (pending.Count == 0) return;

            var missing = new List<int>();
            foreach (var code in pending)
            {
                var have = false;
                for (var i = 0; i < Assets.Count; i++)
                {
                    if (Assets[i] != null && Assets[i].HasCharacter(code))
                    {
                        have = true;
                        break;
                    }
                }
                if (!have) missing.Add(code);
            }
            if (missing.Count == 0) return;

            UnityEngine.Debug.Log("[SP2Chinese] dictionary reload: " + missing.Count +
                                  " new glyphs, extending atlas");
            for (var i = 0; i < Assets.Count && missing.Count > 0; i++)
            {
                if (Assets[i] == null) continue;
                var left = Populate(Assets[i], missing);
                TotalGlyphs += missing.Count - left.Count;
                missing = left;
            }

            if (missing.Count > 0)
            {
                UnityEngine.Debug.LogWarning("[SP2Chinese] atlas full, rebuilding; " +
                                             missing.Count + " glyphs still missing");
                Setup(usedCharacters);
            }
        }

        private static string PickFont()
        {
            foreach (var f in Files)
                if (File.Exists(f))
                    return f;
            return null;
        }

        private static TMPro.TMP_FontAsset Make(string path, int size)
        {
            if (path == null) return null;
            try
            {
                return TMPro.TMP_FontAsset.CreateFontAsset(path, 0, SamplingPointSize, AtlasPadding,
                    UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, size, size);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[SP2Chinese] font creation failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Adds <paramref name="codes"/> to the atlas, returns the ones that did not fit.</summary>
        private static List<int> Populate(TMPro.TMP_FontAsset asset, List<int> codes)
        {
            asset.atlasPopulationMode = TMPro.AtlasPopulationMode.Dynamic;

            var leftover = new List<int>();
            for (var i = 0; i < codes.Count; i += BatchSize)
            {
                var n = Math.Min(BatchSize, codes.Count - i);
                var batch = new uint[n];
                for (var j = 0; j < n; j++) batch[j] = (uint)codes[i + j];

                uint[] missing = null;
                try
                {
                    asset.TryAddCharacters(batch, out missing, false);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("[SP2Chinese] TryAddCharacters failed: " + e.Message);
                    for (var j = 0; j < n; j++) leftover.Add(codes[i + j]);
                    continue;
                }

                if (missing == null) continue;
                foreach (var m in missing)
                {
                    var c = (int)m;
                    if (!leftover.Contains(c)) leftover.Add(c);
                }
            }

            // Keep the request order stable for the next atlas.
            leftover.Sort();
            return leftover;
        }

        private static void Register()
        {
            var list = TMPro.TMP_Settings.fallbackFontAssets;
            if (list == null) list = new List<TMPro.TMP_FontAsset>();

            for (var i = 0; i < Assets.Count; i++)
            {
                var asset = Assets[i];
                asset.name = "SP2ZhFallback" + (i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture));

                var table = asset.fontWeightTable;
                if (table != null)
                {
                    for (var w = 0; w < table.Length; w++)
                    {
                        var pair = new TMPro.TMP_FontWeightPair();
                        pair.regularTypeface = asset;
                        pair.italicTypeface = asset;
                        table[w] = pair;
                    }
                }

                if (!list.Contains(asset)) list.Add(asset);
            }

            TMPro.TMP_Settings.fallbackFontAssets = list;
        }
    }

    /// <summary>
    /// Some text on screen is *data*, not *prose*, and translating it breaks the game.
    ///
    /// Two independent guards, because two different things go wrong:
    ///
    /// 1. <b>Editable input text</b> (<see cref="IsExempt(TMPro.TMP_Text)"/>) — the contents are
    ///    part property values (numbers, enum names, attribute names).
    ///    <c>TMP_InputField.text</c> reads back the internal <c>m_Text</c> rather than the rendered
    ///    label, so a translated label does not corrupt the stored value — but the player reads the
    ///    field and retypes what they see elsewhere, so it is still a trap.
    ///
    /// 2. <b>Machine identifiers</b> (<see cref="MachineIdentifiers"/>) — Overload's attribute-name
    ///    column and its autocomplete suggestion list display raw XML attribute names
    ///    (<c>powerMultiplier</c>, <c>burnerTemp</c>, <c>seeker</c>) that the player must retype by
    ///    hand into "New Property Name". The game matches them byte for byte, so they must stay
    ///    verbatim: translating one is how you end up typing 推力倍率 into a field that only accepts
    ///    powerMultiplier.
    ///
    /// Guard 2 is a <b>content</b> test on purpose. Two earlier attempts anchored on the widget
    /// tree — first on <c>Transform.name</c>, then on <c>Widget.Id</c> walked through
    /// <c>Widget.Parent</c> — and both failed, because the ids the game looks up
    /// (<c>attributes-container</c>, <c>name-autocomplete</c>, <c>suggestions-list</c>) live on widget
    /// nodes that are not ancestors of the text component in the transform hierarchy. Matching the
    /// string itself cannot be defeated by tree shape.
    /// </summary>
    internal static class MachineText
    {
        /// <summary>True when this text is the editable value of an input field (not its placeholder).</summary>
        internal static bool IsExempt(TMPro.TMP_Text t)
        {
            if (t == null) return true;

            var field = t.GetComponentInParent<TMPro.TMP_InputField>(true);

            // ReferenceEquals on textComponent, not GetComponentInParent alone: the placeholder is
            // also a descendant of the field but is never read back as a value, so it stays
            // translatable.
            return field != null && ReferenceEquals(field.textComponent, t);
        }

        /// <summary>Legacy uGUI equivalent of the above.</summary>
        internal static bool IsExempt(UnityEngine.UI.Text t)
        {
            if (t == null) return true;

            var field = t.GetComponentInParent<UnityEngine.UI.InputField>(true);
            return field != null && ReferenceEquals(field.textComponent, t);
        }

        /// <summary>Single decision used by every hook and by the periodic sweep.</summary>
        internal static bool Skip(string s, UnityEngine.Object owner)
        {
            if (MachineIdentifiers.Contains(s)) return true;
            if (owner is TMPro.TMP_Text t) return IsExempt(t);
            if (owner is UnityEngine.UI.Text u) return IsExempt(u);
            return false;
        }
    }

    internal static class TmpHook
    {
        /// <summary>True for CJK ideographs plus the punctuation / fullwidth blocks that go with them.</summary>
        internal static bool NeedsCjkFont(string s)
        {
            foreach (var c in s)
            {
                if (c >= '\u2e80' && c <= '\u9fff') return true;   // radicals, punctuation, kana, ideographs
                if (c >= '\uf900' && c <= '\ufaff') return true;   // compatibility ideographs
                if (c >= '\uff00' && c <= '\uffef') return true;   // fullwidth forms
                if (c >= '\u3000' && c <= '\u303f') return true;   // CJK symbols and punctuation
            }
            return false;
        }

        internal static void Force(TMPro.TMP_Text t, string s)
        {
            var font = FontLoader.Assets.Count > 0 ? FontLoader.Assets[0] : null;
            if (font == null || s == null) return;
            if (!NeedsCjkFont(s)) return;
            if (t.font != font) t.font = font;
            if (t.fontWeight != TMPro.FontWeight.Bold) t.fontWeight = TMPro.FontWeight.Bold;
        }
    }

    /// <summary>
    /// Translates a text component the moment it comes into existence.
    ///
    /// This is what replaced the old every-second full sweep. Serialized prefab text never
    /// goes through <c>set_text</c> - when a dialog prefab is instantiated its TMP component
    /// deserializes <c>m_Text</c> directly and nobody calls a setter - so the sweep existed
    /// to catch exactly these.
    ///
    /// The patch target matters more than it looks: <c>TMP_Text</c> does NOT declare Awake
    /// (that v1.4.3 mistake made HarmonyX log "Could not find method for type TMPro.TMP_Text
    /// and name Awake" and abort the patch chain, leaving the mod loaded but every hook
    /// unapplied). The method lives on the concrete <c>TextMeshProUGUI</c>, and it does not
    /// even call <c>base.Awake()</c> - so patching UIBehaviour would miss TMP entirely.
    /// </summary>
    [HarmonyPatch(typeof(TMPro.TextMeshProUGUI), "Awake")]
    internal static class TmpAwakePatch
    {
        private static void Postfix(TMPro.TextMeshProUGUI __instance)
        {
            Translate(__instance);
        }

        internal static void Translate(TMPro.TMP_Text t)
        {
            if (Translator.I == null || t == null) return;
            var s = t.text;
            if (string.IsNullOrEmpty(s) || MachineText.Skip(s, t)) return;
            var r = Translator.I.Apply(s);
            if (r != s) t.text = r;
        }
    }

    /// <summary>3D TextMeshPro components, if the game ever uses them.</summary>
    [HarmonyPatch(typeof(TMPro.TextMeshPro), "Awake")]
    internal static class Tmp3DAwakePatch
    {
        private static void Postfix(TMPro.TextMeshPro __instance)
        {
            TmpAwakePatch.Translate(__instance);
        }
    }

    /// <summary>Legacy uGUI Text. It declares no Awake of its own, so the inherited
    /// UIBehaviour.Awake is the one that actually runs - and unlike TMP, this one is
    /// reached through the normal inheritance chain.</summary>
    [HarmonyPatch(typeof(UnityEngine.EventSystems.UIBehaviour), "Awake")]
    internal static class UiAwakePatch
    {
        private static void Postfix(UnityEngine.EventSystems.UIBehaviour __instance)
        {
            var t = __instance as UnityEngine.UI.Text;
            if (t == null) return;
            if (Translator.I == null) return;
            var s = t.text;
            if (string.IsNullOrEmpty(s) || MachineText.Skip(s, t)) return;
            var r = Translator.I.Apply(s);
            if (r != s) t.text = r;
        }
    }

    [HarmonyPatch(typeof(TMPro.TMP_Text), "set_text")]
    internal static class TmpTextPatch
    {
        private static void Prefix(TMPro.TMP_Text __instance, ref string value)
        {
            if (MachineText.Skip(value, __instance)) return;
            value = Translator.I.Apply(value);
        }

        private static void Postfix(TMPro.TMP_Text __instance, string value)
        {
            TmpHook.Force(__instance, value);
        }
    }

    [HarmonyPatch(typeof(TMPro.TMP_Text), "SetText", new Type[] { typeof(string) })]
    internal static class TmpSetTextPatch
    {
        private static void Prefix(TMPro.TMP_Text __instance, ref string sourceText)
        {
            if (MachineText.Skip(sourceText, __instance)) return;
            sourceText = Translator.I.Apply(sourceText);
        }

        private static void Postfix(TMPro.TMP_Text __instance, string sourceText)
        {
            TmpHook.Force(__instance, sourceText);
        }
    }

    /// <summary>
    /// The overload the property setter and most of the game's own calls funnel through.
    ///
    /// Missing this one cost a whole round: the craft Instructions dialog stayed English while
    /// every other panel translated, and untranslated.txt had NO record of the text at all -
    /// proof it never reached the translator. TMP declares SetText(string), SetText(string, bool)
    /// and a family of SetText(string, float...) format helpers; a widget that fills an empty
    /// prefab by calling SetText(text, syncInputBox) bypasses set_text entirely.
    /// </summary>
    [HarmonyPatch(typeof(TMPro.TMP_Text), "SetText", new Type[] { typeof(string), typeof(bool) })]
    internal static class TmpSetTextBoolPatch
    {
        private static void Prefix(TMPro.TMP_Text __instance, ref string sourceText)
        {
            if (Translator.I == null || string.IsNullOrEmpty(sourceText)) return;
            if (MachineText.Skip(sourceText, __instance)) return;
            sourceText = Translator.I.Apply(sourceText);
        }

        private static void Postfix(TMPro.TMP_Text __instance, string sourceText)
        {
            TmpHook.Force(__instance, sourceText);
        }
    }

    [HarmonyPatch(typeof(TMPro.TMP_Text), "SetText", new Type[] { typeof(StringBuilder) })]
    internal static class TmpSetSbPatch
    {
        private static void Prefix(TMPro.TMP_Text __instance, StringBuilder sourceText)
        {
            var before = sourceText.ToString();
            if (MachineText.Skip(before, __instance)) return;
            var after = Translator.I.Apply(before);
            if (after == before) return;
            sourceText.Clear();
            sourceText.Append(after);
        }

        private static void Postfix(TMPro.TMP_Text __instance, StringBuilder sourceText)
        {
            TmpHook.Force(__instance, sourceText.ToString());
        }
    }

    [HarmonyPatch(typeof(UnityEngine.UI.Text), "set_text")]
    internal static class UiTextPatch
    {
        private static void Prefix(Text __instance, ref string value)
        {
            if (MachineText.Skip(value, __instance)) return;
            value = Translator.I.Apply(value);
        }
    }
}