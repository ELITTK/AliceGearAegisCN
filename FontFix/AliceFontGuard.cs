using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

// UGUI divides both generated vertices and preferred sizes by pixelsPerUnit.
// Bound raster size through that common property to preserve logical layout.
[BepInPlugin("local.alice.fontguard", "Alice Font Guard", "0.9.0")]
[BepInDependency("gravydevsupreme.xunity.autotranslator")]
public sealed class AliceFontGuard : BaseUnityPlugin
{
    private static ManualLogSource Log;
    private static readonly HashSet<int> Seen = new HashSet<int>();
    private static int Samples;
    private static bool PreserveNativeAscii;
    [ThreadStatic] private static bool SwitchingFont;
    private static int NativeSamples;
    private sealed class FontState
    {
        public Font Original;
        public Font Replacement;
        public int OriginalSize;
        public int ReplacementSize;
        public bool Enabled;
        public bool Logged;
        public int TargetHeight;
    }
    private sealed class GlyphSet
    {
        public readonly HashSet<int> Indices = new HashSet<int>();
        public GlyphSet(Font font)
        {
            foreach (CharacterInfo glyph in font.characterInfo)
                Indices.Add(glyph.index);
        }
    }
    // Weak keys let destroyed UI components and their font references be collected.
    private static readonly ConditionalWeakTable<Text, FontState> NativeFonts = new ConditionalWeakTable<Text, FontState>();
    private static readonly ConditionalWeakTable<Font, GlyphSet> NativeGlyphs = new ConditionalWeakTable<Font, GlyphSet>();
    private static bool RecoverClippedLabels;
    private static bool FixCustomRawImages;
    [ThreadStatic] private static bool RetryingText;
    private static readonly HashSet<int> RecoveredLabels = new HashSet<int>();
    private static readonly HashSet<string> FixedImageShaders = new HashSet<string>();
    private static readonly HashSet<int> PendingGlyphRebuilds = new HashSet<int>();
    private static readonly Dictionary<int, int> GlyphRetries = new Dictionary<int, int>();
    private static readonly HashSet<int> PrewarmedFonts = new HashSet<int>();
    private static bool MenuLabelOverflow;
    private static readonly HashSet<int> MenuSeen = new HashSet<int>();
    private static readonly List<Text> PendingMenuChecks = new List<Text>();
    private float nextMenuCheck;
    private Harmony harmony;
    private static int MaxRasterSize = 96; private static int ZeroSize = 32; private static int OriginalSamples;

    private void Awake()
    {
        Log = Logger;
        PreserveNativeAscii = Config.Bind("Font", "PreserveNativeAscii", true,
            "Keep bitmap font metrics for plain ASCII fully supported by the original font; switch back for Chinese.").Value;
        RecoverClippedLabels = Config.Bind("Layout", "RecoverClippedLabels", true,
            "Retry short single-line replacement text only when truncation generated no visible geometry.").Value;
        FixCustomRawImages = Config.Bind("Images", "FixCustomRawImages", true,
            "Use the actual albedo property for known custom RawImage shaders; preserve explicit textures.").Value;
        MenuLabelOverflow = Config.Bind("Layout", "MenuLabelOverflow", true,
            "Allow profile-menu header labels to render beyond their 30-unit text boxes.").Value;
        MaxRasterSize = Mathf.Clamp(Config.Bind("Font", "MaxRasterSize", 96,
            "Maximum glyph pixel size. UI layout font sizes are preserved.").Value, 32, 256);
        ZeroSize = Mathf.Clamp(Config.Bind("Font", "ZeroSizeFallback", 32,
            "Logical font size used when a bitmap text has size zero.").Value, 8, 96);
        harmony = new Harmony("local.alice.fontguard");
        Type helper = AccessTools.TypeByName("XUnity.AutoTranslator.Plugin.Core.Fonts.FontHelper");
        if (helper == null) throw new InvalidOperationException("XUnity font helper not loaded");
        harmony.Patch(AccessTools.Method(helper, "GetTextFont", new Type[] { typeof(int) }),
            prefix: new HarmonyMethod(typeof(AliceFontGuard), "CreateFontPrefix"),
            postfix: new HarmonyMethod(typeof(AliceFontGuard), "PrewarmDynamicFont"));
        harmony.Patch(AccessTools.PropertySetter(typeof(Text), "font"),
            prefix: new HarmonyMethod(typeof(AliceFontGuard), "FontSetterPrefix"));
        harmony.Patch(AccessTools.PropertySetter(typeof(Text), "text"),
            postfix: new HarmonyMethod(typeof(AliceFontGuard), "TextSetterPostfix"));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Text), "pixelsPerUnit"),
            postfix: new HarmonyMethod(typeof(AliceFontGuard), "PixelsPerUnitPostfix"));
        harmony.Patch(AccessTools.Method(typeof(Text), "GetGenerationSettings", new Type[] { typeof(Vector2) }),
            postfix: new HarmonyMethod(typeof(AliceFontGuard), "GenerationPostfix"));

        harmony.Patch(AccessTools.Method(typeof(TextGenerator), "PopulateWithErrors",
                new Type[] { typeof(string), typeof(TextGenerationSettings), typeof(GameObject) }),
            postfix: new HarmonyMethod(typeof(AliceFontGuard), "RecoverEmptyText"));
        harmony.Patch(AccessTools.PropertyGetter(typeof(RawImage), "mainTexture"),
            prefix: new HarmonyMethod(typeof(AliceFontGuard), "RawImageTexturePrefix"));
        Logger.LogInfo("Font guard active; max glyph raster size=" + MaxRasterSize
            + "; clipped label recovery=" + RecoverClippedLabels + "; custom image compatibility=" + FixCustomRawImages);
    }


    // Check after rendering, rather than creating extra TextGenerators inside a
    // generation hook (which could itself trigger font texture rebuilds).
    private void LateUpdate()
    {
        if (Time.unscaledTime < nextMenuCheck) return;
        nextMenuCheck = Time.unscaledTime + 1f;
        foreach (Text text in PendingMenuChecks)
        {
            if (text == null) continue;
            Log.LogInfo("MENU RENDER path=" + PathOf(text.transform)
                + " active=" + text.isActiveAndEnabled
                + " vertices=" + text.cachedTextGenerator.vertexCount
                + " visibleChars=" + text.cachedTextGenerator.characterCountVisible
                + " preferredHeight=" + text.preferredHeight
                + " rectHeight=" + text.rectTransform.rect.height
                + " alpha=" + text.color.a
                + " canvasAlpha=" + text.canvasRenderer.GetAlpha()
                + " culled=" + text.canvasRenderer.cull);
        }
        PendingMenuChecks.Clear();
    }

    private static bool IsMenuLabel(Text text)
    {
        if (text.name != "label") return false;
        return PathOf(text.transform).IndexOf("CfgMenu(Clone)/front/header/", StringComparison.Ordinal) >= 0;
    }

    private static void AdjustMenuLabel(Text text, ref TextGenerationSettings settings)
    {
        if (!IsMenuLabel(text)) return;
        VerticalWrapMode before = settings.verticalOverflow;
        if (MenuLabelOverflow) settings.verticalOverflow = VerticalWrapMode.Overflow;
        if (MenuSeen.Count >= 40 || !MenuSeen.Add(text.GetInstanceID())) return;
        string value = (text.text ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
        if (value.Length > 100) value = value.Substring(0, 100);
        Log.LogInfo("MENU LABEL path=" + PathOf(text.transform) + " text=[" + value + "]"
            + " size=" + settings.fontSize + " extent=" + settings.generationExtents
            + " vertical=" + before + " -> " + settings.verticalOverflow
            + " horizontal=" + settings.horizontalOverflow
            + " font=" + settings.font.name);
        PendingMenuChecks.Add(text);
    }
    // This Unity build emits 4 vertices per glyph and 0 when fully clipped.
    // UGUI calls PopulateWithErrors immediately before consuming the generated
    // vertices. It disables its font texture callback while doing so. Retrying
    // this same generator via Populate avoids recursively invoking this hook.
    private static void RecoverEmptyText(TextGenerator __instance, string __0,
        TextGenerationSettings __1, GameObject __2, ref bool __result)
    {
        // A zero-vertex retry is meaningful for requested CJK glyphs only.
        // ASCII/decorative placeholder labels must keep their native bitmap
        // path; retrying them with a dynamic font changes their visual scale.
        if (__result && __instance.vertexCount == 0 && __1.font != null && __1.font.dynamic
            && !ContainsCjk(__0)) return;
        if (!RecoverClippedLabels || RetryingText || !__result || __instance.vertexCount > 0
            || __1.verticalOverflow != VerticalWrapMode.Truncate || __1.generationExtents.y <= 0
            || __1.font == null || !__1.font.dynamic || __1.font.name.IndexOf("Microsoft YaHei", StringComparison.OrdinalIgnoreCase) < 0
            || String.IsNullOrWhiteSpace(__0) || __0.Length > 200 || __0.IndexOf('\n') >= 0 || __0.IndexOf('\r') >= 0)
            return;
        TextGenerationSettings retry = __1;
        retry.verticalOverflow = VerticalWrapMode.Overflow;
        int before = __instance.vertexCount;
        RetryingText = true;
        try
        {
            bool success = __instance.Populate(__0, retry);
            if (success && __instance.vertexCount > 0)
            {
                __result = true;
                if (__2 != null && RecoveredLabels.Count < 80 && RecoveredLabels.Add(__2.GetInstanceID()))
                    Log.LogInfo("RECOVERED LABEL path=" + PathOf(__2.transform)
                        + " length=" + __0.Length + " size=" + __1.fontSize
                        + " height=" + __1.generationExtents.y
                        + " vertices=" + before + " -> " + __instance.vertexCount);
            }
            else
            {
                // A blank caused by anything other than clipping keeps its
                // original generation result and original settings.
                __result = __instance.Populate(__0, __1);
            }
        }
        finally { RetryingText = false; }
    }

    private static bool ContainsCjk(string value)
    {
        if (String.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
            if ((c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF)) return true;
        return false;
    }

    private static bool RawImageTexturePrefix(RawImage __instance, ref Texture __result)
    {
        if (!FixCustomRawImages || __instance == null || __instance.texture != null) return true;
        Material material = __instance.material;
        if (material == null || material.shader == null || material.HasProperty("_MainTex")) return true;
        string shader = material.shader.name;
        if (shader == "Pyd/UI/SimpleFxAnim1P" && material.HasProperty("_AlbedoTex"))
        {
            __result = material.GetTexture("_AlbedoTex");
            if (__result == null) __result = Texture2D.whiteTexture;
        }
        else if (shader == "Common/LoadingEffect/MiniLoadingFx")
        {
            // This effect has no standard main texture. Match UGUI's existing
            // white-texture fallback without querying a nonexistent property.
            __result = Texture2D.whiteTexture;
        }
        else return true;
        if (FixedImageShaders.Add(shader))
            Log.LogInfo("RAWIMAGE COMPAT shader=" + shader + " texture=" + __result.name);
        return false;
    }

    private static void CreateFontPrefix(ref int __0)
    {
        if (__0 <= 0)
        {
            Log.LogInfo("Normalized dynamic font creation size " + __0 + " -> " + ZeroSize);
            __0 = ZeroSize;
        }
    }

    private static void PrewarmDynamicFont(int __0, ref Font __result)
    {
        if (__result == null || !__result.dynamic || __result.name.IndexOf("Microsoft YaHei", StringComparison.OrdinalIgnoreCase) < 0) return;
        int id = __result.GetInstanceID();
        if (!PrewarmedFonts.Add(id)) return;
        int size = Math.Max(8, __0);
        string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz.Phrase";
        try
        {
            string root = Paths.GameRootPath;
            string file = Path.Combine(root, "BepInEx", "Translation", "zh", "Text", "UI.txt");
            if (File.Exists(file))
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int equal = line.IndexOf('=');
                    if (equal < 0) continue;
                    foreach (char c in line.Substring(equal + 1))
                        if (c >= 0x2E80 && c <= 0x9FFF) chars += c;
                }
            // Request in bounded chunks; one giant request can exceed Unity's
            // temporary request buffer and silently drop the tail.
            for (int i = 0; i < chars.Length; i += 128)
                __result.RequestCharactersInTexture(chars.Substring(i, Math.Min(128, chars.Length - i)), size, FontStyle.Normal);
            Log.LogInfo("PREWARM FONT name=" + __result.name + " size=" + size + " chars=" + chars.Length);
        }
        catch (Exception ex) { Log.LogWarning("PREWARM FONT failed: " + ex.Message); }
    }

    private static void FontSetterPrefix(Text __instance, ref Font __0)
    {
        if (SwitchingFont) return;
        FontState state;
        if (__0 == null || !__0.dynamic || __0.name.IndexOf("Microsoft YaHei", StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (NativeFonts.TryGetValue(__instance, out state))
            {
                // Explicit font restoration (including Alt+F) ends our override.
                state.Enabled = false;
                if (__0 == state.Original && __instance.font == state.Replacement)
                    __instance.fontSize = state.OriginalSize;
            }
            return;
        }
        if (PreserveNativeAscii)
        {
            Font original = __instance.font;
            if (original != null && !original.dynamic)
            {
                state = NativeFonts.GetValue(__instance, delegate(Text ignored) { return new FontState(); });
                state.Original = original;
                state.OriginalSize = __instance.fontSize;
                state.TargetHeight = NativeHeight(original);
            }
            if (NativeFonts.TryGetValue(__instance, out state) && state.Original != null)
            {
                state.Replacement = __0;
                state.ReplacementSize = __instance.fontSize > 0 ? __instance.fontSize : InferredSize(__instance);
                state.Enabled = true;
                if (CanUseNativeText(__instance.text, state.Original))
                {
                    __0 = state.Original;
                    __instance.fontSize = state.OriginalSize;
                    LogNative(__instance, state);
                    return;
                }
            }
        }
        Font previous = __instance.font;
        if (previous != null && previous != __0 && OriginalSamples < 30)
        {
            OriginalSamples++;
            string metrics = "";
            if (!previous.dynamic)
            {
                CharacterInfo[] chars = previous.characterInfo;
                for (int i = 0, count = 0; i < chars.Length && count < 5; i++)
                    if (chars[i].index >= 0x3000)
                    {
                        metrics += " U+" + chars[i].index.ToString("X4") + ":height=" + chars[i].glyphHeight + ",advance=" + chars[i].advance;
                        count++;
                    }
            }
            Log.LogInfo("ORIGINAL FONT path=" + PathOf(__instance.transform) + " font=" + previous.name
                + " dynamic=" + previous.dynamic + " base=" + previous.fontSize + " textSize=" + __instance.fontSize + metrics);
        }
        // Keep the component original layout size; GenerationPostfix applies a content-aware fallback.
    }

    private static int NativeHeight(Font font)
    {
        if (font == null) return 0;
        int total = 0, count = 0;
        foreach (CharacterInfo c in font.characterInfo)
            if (c.glyphHeight > 0 && c.index >= 0x3000) { total += c.glyphHeight; count++; }
        if (count == 0) foreach (CharacterInfo c in font.characterInfo)
            if (c.glyphHeight > 0 && c.index >= 32 && c.index <= 126) { total += c.glyphHeight; count++; }
        return count == 0 ? 0 : Math.Max(1, total / count);
    }
    private static int InferredSize(Text text)
    {
        FontState state;
        if (!NativeFonts.TryGetValue(text, out state) || state.TargetHeight <= 0) return ZeroSize;
        return Mathf.Clamp(Mathf.RoundToInt(ZeroSize * (state.TargetHeight / 30f)), 8, 48);
    }
    [HarmonyPriority(Priority.Last)]
    private static void TextSetterPostfix(Text __instance)
    {
        FontState state;
        if (SwitchingFont || !PreserveNativeAscii || !NativeFonts.TryGetValue(__instance, out state)
            || !state.Enabled || state.Original == null || state.Replacement == null) return;
        bool native = CanUseNativeText(__instance.text, state.Original);
        Font desired = native ? state.Original : state.Replacement;
        if (__instance.font == desired) return;
        if (__instance.font != state.Original && __instance.font != state.Replacement) return;
        if (__instance.font == state.Replacement && __instance.fontSize > 0) state.ReplacementSize = __instance.fontSize;
        SwitchingFont = true;
        try { __instance.fontSize = native ? state.OriginalSize : Math.Max(1, state.ReplacementSize); __instance.font = desired; }
        finally { SwitchingFont = false; }
        if (native) LogNative(__instance, state);
    }
    private static void LogNative(Text text, FontState state)
    {
        if (state.Logged || NativeSamples >= 100) return;
        state.Logged = true; NativeSamples++;
        Log.LogInfo("NATIVE ASCII path=" + PathOf(text.transform) + " text=[" + (text.text ?? "") + "] font=" + state.Original.name + " size=" + state.OriginalSize);
    }
    private static bool IsEvolutionAction(Text text)
    {
        if (text == null || text.text == null) return false;
        string path = PathOf(text.transform), value = text.text;
        return (path.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
            && (value.IndexOf("アップグレード", StringComparison.Ordinal) >= 0 || value.IndexOf("停止", StringComparison.Ordinal) >= 0 || value.IndexOf("確認", StringComparison.Ordinal) >= 0);
    }
    private static bool CanUseNativeText(string value, Font font)
    {
        if (font == null || font.dynamic || String.IsNullOrWhiteSpace(value)) return false;
        // Preserve the original resource whenever every character is present.
        // This covers untranslated Japanese as well as ASCII and deco glyphs.
        foreach (char c in value)
            if (c == '<' || c == '>' || c == '\r' || c == '\n') return false;
        GlyphSet glyphs = NativeGlyphs.GetValue(font, delegate(Font f) { return new GlyphSet(f); });
        foreach (char c in value)
            if (Char.IsWhiteSpace(c)) continue;
            else if (!glyphs.Indices.Contains(c)) return false;
        return true;
    }
    private static bool IsSmallLabSubtitle(Text text)
    {
        if (text == null || text.text == null) return false;
        string path = PathOf(text.transform);
        if (path.IndexOf("/ToLab/", StringComparison.Ordinal) < 0 && path.IndexOf("/ToAssistCharaLst/", StringComparison.Ordinal) < 0) return false;
        foreach (char c in text.text)
            if (c < 32 || c > 126) return false;
        return text.text.Trim().Length > 0;
    }

    private static bool IsReplacement(Text text)
    {
        if (text == null || text.font == null || !text.font.dynamic) return false;
        string name = text.font.name;
        return name != null && name.IndexOf("Microsoft YaHei", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void PixelsPerUnitPostfix(Text __instance, ref float __result)
    {
        if (!IsReplacement(__instance)) return;
        int largest = __instance.fontSize > 0 ? __instance.fontSize : ZeroSize;
        if (__instance.resizeTextForBestFit)
            largest = Math.Max(largest, __instance.resizeTextMaxSize);
        if (__result > (float)MaxRasterSize / largest)
            __result = (float)MaxRasterSize / largest;
    }

    private static void GenerationPostfix(Text __instance, ref TextGenerationSettings __result)
    {
        if (!IsReplacement(__instance)) return;
        if (__result.fontSize <= 0) __result.fontSize = InferredSize(__instance);
        if (__result.resizeTextForBestFit)
        {
            __result.resizeTextMinSize = Math.Max(1, __result.resizeTextMinSize);
            __result.resizeTextMaxSize = Math.Max(__result.resizeTextMinSize, __result.resizeTextMaxSize);
        }
        AdjustMenuLabel(__instance, ref __result);
        // The maintenance-room button uses a real ASCII subtitle instead of the one-character deco glyph used by its siblings.
        if (IsSmallLabSubtitle(__instance) && __result.fontSize > 10) __result.fontSize = 8;
        if (IsEvolutionAction(__instance))
        {
            // BestFit shrinks Japanese text after the font swap because YaHei has wider metrics.
            __result.resizeTextForBestFit = false;
            if (__result.fontSize < ZeroSize) __result.fontSize = ZeroSize;
        }
        if (Samples >= 100 || !Seen.Add(__instance.GetInstanceID())) return;
        Samples++;
        float canvasScale = __instance.canvas != null ? __instance.canvas.scaleFactor : 1;
        Log.LogInfo("FONT SAMPLE path=" + PathOf(__instance.transform)
            + " font=" + __result.font.name + " base=" + __result.font.fontSize
            + " size=" + __result.fontSize + " canvasScale=" + canvasScale
            + " guardedScale=" + __result.scaleFactor
            + " bestFit=" + __result.resizeTextForBestFit
            + " min=" + __result.resizeTextMinSize + " max=" + __result.resizeTextMaxSize
            + " extent=" + __result.generationExtents);
    }

    private static string PathOf(Transform transform)
    {
        string path = transform.name;
        for (int i = 0; transform.parent != null && i < 10; i++)
        {
            transform = transform.parent;
            path = transform.name + "/" + path;
        }
        return path;
    }
}
