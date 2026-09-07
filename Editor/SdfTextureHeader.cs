using System;
using UnityEditor;
using UnityEngine;

namespace SDFUI.Editor
{
    [InitializeOnLoad]
    internal static class SdfTextureHeader
    {
        private static readonly string[] SizeLabels = { "64", "128", "256", "512", "1024" };
        private static readonly int[] Sizes = { 64, 128, 256, 512, 1024 };
        static SdfTextureHeader() => UnityEditor.Editor.finishedDefaultHeaderGUI += Draw;

        private static void Draw(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1 || !(editor.target is TextureImporter || editor.target is Texture2D || editor.target is Sprite)) return;
            string path = AssetDatabase.GetAssetPath(editor.target);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (!importer || importer.textureType != TextureImporterType.Sprite) return;
            var settings = SdfTextureSettings.Get(path);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("SDF Image", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            settings.enabled = EditorGUILayout.Toggle("Generate SDF", settings.enabled);
            if (settings.enabled)
            {
                settings.maxSize = EditorGUILayout.IntPopup("Maximum Bake Size", settings.maxSize,
                    SizeLabels, Sizes);
                settings.padding = EditorGUILayout.IntSlider("Padding", settings.padding, 4, 128);
                settings.range = EditorGUILayout.Slider("Distance Range", settings.range, 4, settings.padding);
                settings.alphaThreshold = EditorGUILayout.Slider("Alpha Threshold", settings.alphaThreshold, 0.01f, 0.99f);
            }
            if (EditorGUI.EndChangeCheck()) SdfTextureSettings.Set(path, settings);
            string status = SdfBakeQueue.GetStatus(path);
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
            if (status == "Queued" || status.StartsWith("Baking ", StringComparison.Ordinal)) editor.Repaint();
        }
    }
}
