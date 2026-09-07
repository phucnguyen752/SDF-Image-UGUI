using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SDFUI.Editor
{
    /// <summary>Opt-in metadata stored alongside the original texture, preserving other importer user data.</summary>
    [Serializable]
    public sealed class SdfTextureSettings
    {
        private const string Marker = "\n[SDF_IMAGE_V1]\n";
        private const string EndMarker = "\n[/SDF_IMAGE_V1]\n";
        private const int MaximumBlockLength = 16 * 1024;
        public bool enabled;
        public int maxSize = 512;
        public int padding = 32;
        public float range = 32;
        public float alphaThreshold = 0.5f;

        public static SdfTextureSettings Get(string path) => Get(AssetImporter.GetAtPath(path) as TextureImporter);

        internal static SdfTextureSettings Get(TextureImporter importer)
        {
            string text = importer ? importer.userData : string.Empty;
            FindBlock(text, out _, out int jsonStart, out int jsonEnd, out _, out _);
            var settings = ReadSettings(text, jsonStart, jsonEnd) ?? new SdfTextureSettings();
            settings.Clamp();
            return settings;
        }

        private static SdfTextureSettings ReadSettings(string text, int start, int end)
        {
            if (end < 0) return null;
            try
            {
                return JsonUtility.FromJson<SdfTextureSettings>(text.Substring(start, end - start));
            }
            catch (ArgumentException) { return null; }
        }

        public static void Set(string path, SdfTextureSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!importer || importer.textureType != TextureImporterType.Sprite || !path.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("Select a Sprite imported from an image inside Assets.", nameof(path));
            settings.Clamp();
            string previous = importer.userData ?? string.Empty;
            FindBlock(previous, out int marker, out int jsonStart, out int jsonEnd, out int blockEnd, out bool terminated);
            if (!terminated && ReadSettings(previous, jsonStart, jsonEnd) == null) blockEnd = -1;
            string block = Marker + JsonUtility.ToJson(settings) + EndMarker;
            // An unterminated damaged block has no safe deletion boundary. Preserve it and append a fresh block.
            string updated = blockEnd < 0 ? previous + block : previous.Substring(0, marker) + block + previous.Substring(blockEnd);
            if (previous != updated)
            {
                Undo.RecordObject(importer, "Change SDF Generation");
                importer.userData = updated;
                EditorUtility.SetDirty(importer);
                AssetDatabase.WriteImportSettingsIfDirty(path);
            }
            if (settings.enabled) SdfBakeQueue.Enqueue(path);
            else SdfBakeQueue.Cancel(path);
        }

        private static void FindBlock(string text, out int marker, out int jsonStart, out int jsonEnd, out int blockEnd, out bool terminated)
        {
            marker = string.IsNullOrEmpty(text) ? -1 : text.LastIndexOf(Marker, StringComparison.Ordinal);
            jsonStart = jsonEnd = blockEnd = -1;
            terminated = false;
            if (marker < 0) return;
            int start = marker + Marker.Length;
            int limit = start + Math.Min(MaximumBlockLength, text.Length - start);
            int terminator = text.IndexOf(EndMarker, start, limit - start, StringComparison.Ordinal);
            terminated = terminator >= 0;
            jsonStart = start;
            while (jsonStart < limit && char.IsWhiteSpace(text[jsonStart])) jsonStart++;
            if (jsonStart < limit && text[jsonStart] == '{')
            {
                int depth = 0;
                bool quoted = false, escaped = false;
                for (int i = jsonStart; i < limit; i++)
                {
                    char character = text[i];
                    if (quoted)
                    {
                        if (escaped) escaped = false;
                        else if (character == '\\') escaped = true;
                        else if (character == '"') quoted = false;
                    }
                    else if (character == '"') quoted = true;
                    else if (character == '{') depth++;
                    else if (character == '}' && --depth == 0) { jsonEnd = i + 1; break; }
                }
            }
            // Older metadata has no closing marker; its first complete JSON object is the owned block.
            blockEnd = terminator >= 0 ? terminator + EndMarker.Length : jsonEnd;
            if (terminator < 0 || jsonEnd < 0) return;
            if (jsonEnd > terminator) { jsonEnd = -1; return; }
            for (int i = jsonEnd; i < terminator; i++)
                if (!char.IsWhiteSpace(text[i])) { jsonEnd = -1; return; }
        }

        private void Clamp()
        {
            maxSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(maxSize), 64, 1024);
            padding = Mathf.Clamp(padding, 4, 128);
            range = Mathf.Clamp(float.IsNaN(range) || float.IsInfinity(range) ? 32 : range, 4, padding);
            alphaThreshold = float.IsNaN(alphaThreshold) || float.IsInfinity(alphaThreshold) ? 0.5f : Mathf.Clamp(alphaThreshold, 0.01f, 0.99f);
        }

        public static string Fingerprint(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer ? Fingerprint(path, importer) : string.Empty;
        }

        internal static string Fingerprint(string path, TextureImporter importer)
        {
            var file = new FileInfo(FileUtil.GetPhysicalPath(path));
            if (!file.Exists) return string.Empty;
            var textureSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(textureSettings);
            var input = new StringBuilder("SDFImage-3|");
            input.Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append('|');
            input.Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('|');
            input.Append(JsonUtility.ToJson(textureSettings)).Append('|');
            input.Append(JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings())).Append('|');
            string platform = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString();
            if (platform == "iOS") platform = "iPhone";
            input.Append(JsonUtility.ToJson(importer.GetPlatformTextureSettings(platform))).Append('|');
            input.Append(JsonUtility.ToJson(Get(importer))).Append('|');
            // Source sprite layout is an input. Generated artifacts are intentionally excluded.
#pragma warning disable 618
            foreach (var sprite in importer.spritesheet)
                input.Append(JsonUtility.ToJson(sprite)).Append('|');
#pragma warning restore 618
            return Hash128.Compute(input.ToString()).ToString();
        }
    }
}
