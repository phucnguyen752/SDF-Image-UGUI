using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SDFUI
{
    /// <summary>TMP text with all glyph effects behind all glyph faces. Sizes use Canvas local units.</summary>
    [ExecuteAlways, AddComponentMenu("UI/SDF Text")]
    public sealed class SdfText : TextMeshProUGUI
    {
        [SerializeField] private bool sdfOutlineEnabled = true;
        [SerializeField, Min(0)] private float sdfOutlineWidth = 2;
        [SerializeField, Min(0)] private float sdfOutlineSoftness;
        [SerializeField] private Color sdfOutlineColor = Color.black;
        [SerializeField] private bool sdfShadowEnabled;
        [SerializeField] private Vector2 sdfShadowOffset = new Vector2(2, -2);
        [SerializeField, Min(0)] private float sdfShadowBlur = 2;
        [SerializeField] private float sdfShadowSpread;
        [SerializeField] private Color sdfShadowColor = new Color(0, 0, 0, 0.3f);

        private RectTransform effectRoot;
        private CanvasGroup effectGroup;
        // Unity hot reload must restore ownership together with effectRoot.
        private List<SdfTextLayer> shadows = new List<SdfTextLayer>();
        private List<SdfTextLayer> outlines = new List<SdfTextLayer>();
        private readonly List<CanvasGroup> ownGroups = new List<CanvasGroup>();
        private readonly List<RectMask2D> clipMasks = new List<RectMask2D>();
        private Material faceSource, faceStencil, faceMaterial;
        private bool syncing;
        private bool meshCleared;
        private static Shader effectShader;

        public bool OutlineEnabled { get => sdfOutlineEnabled; set { if (sdfOutlineEnabled == value) return; sdfOutlineEnabled = value; RefreshEffects(); } }
        public float OutlineWidth { get => sdfOutlineWidth; set { sdfOutlineWidth = Positive(value); RefreshEffects(); } }
        public float OutlineSoftness { get => sdfOutlineSoftness; set { sdfOutlineSoftness = Positive(value); RefreshEffects(); } }
        public Color OutlineColor { get => sdfOutlineColor; set { sdfOutlineColor = value; RefreshEffects(); } }
        public bool ShadowEnabled { get => sdfShadowEnabled; set { if (sdfShadowEnabled == value) return; sdfShadowEnabled = value; RefreshEffects(); } }
        public Vector2 ShadowOffset { get => sdfShadowOffset; set { sdfShadowOffset = new Vector2(Finite(value.x), Finite(value.y)); RefreshEffects(); } }
        public float ShadowBlur { get => sdfShadowBlur; set { sdfShadowBlur = Positive(value); RefreshEffects(); } }
        public float ShadowSpread { get => sdfShadowSpread; set { sdfShadowSpread = Finite(value); RefreshEffects(); } }
        public Color ShadowColor { get => sdfShadowColor; set { sdfShadowColor = value; RefreshEffects(); } }

        // These components change the draw/mask domain of the text itself. A parent is supported.
        public bool EffectsSupported => !GetComponent<Canvas>() && !GetComponent<Mask>() && !GetComponent<RectMask2D>();
        private bool HasEffects => sdfOutlineEnabled && sdfOutlineWidth > 0 && sdfOutlineColor.a > 0
            || sdfShadowEnabled && sdfShadowColor.a > 0;

        protected override void OnEnable()
        {
            base.OnEnable();
            if (effectRoot) effectRoot.gameObject.SetActive(true);
            Canvas.preWillRenderCanvases += SyncEffects;
            RefreshEffects();
        }

        protected override void OnDisable()
        {
            Canvas.preWillRenderCanvases -= SyncEffects;
            if (effectRoot) effectRoot.gameObject.SetActive(false);
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            Canvas.preWillRenderCanvases -= SyncEffects;
            if (effectRoot)
            {
                var root = effectRoot.gameObject;
                effectRoot = null;
#if UNITY_EDITOR
                // Scene teardown may already be destroying this sibling. Wait until that
                // operation completes before removing a root left by component removal/Undo.
                if (!Application.isPlaying)
                    UnityEditor.EditorApplication.delayCall += () => { if (root) DestroyImmediate(root); };
                else
#endif
                    Destroy(root);
            }
            Release(faceMaterial);
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            sdfOutlineWidth = Positive(sdfOutlineWidth);
            sdfOutlineSoftness = Positive(sdfOutlineSoftness);
            sdfShadowBlur = Positive(sdfShadowBlur);
            sdfShadowSpread = Finite(sdfShadowSpread);
            sdfShadowOffset = new Vector2(Finite(sdfShadowOffset.x), Finite(sdfShadowOffset.y));
            base.OnValidate();
            RefreshEffects();
        }
#endif

        public void RefreshEffects()
        {
            havePropertiesChanged = true;
            SetVerticesDirty();
            SetMaterialDirty();
        }

        public override Material GetModifiedMaterial(Material baseMaterial)
        {
            Material modified = base.GetModifiedMaterial(baseMaterial);
            if (!isActiveAndEnabled || !EffectsSupported || !IsDistanceField(modified)) return modified;
            faceSource = baseMaterial;
            faceStencil = modified;
            return FaceOnly(faceSource, ref faceMaterial, faceStencil);
        }

        protected override void GenerateTextMesh()
        {
            meshCleared = false;
            bool propertiesChanged = m_havePropertiesChanged;
            base.UpdateMeshPadding();
            m_havePropertiesChanged = propertiesChanged;
            // Padding affects geometry only; TMP still owns advances, wrapping and preferred size.
            // Use the available atlas border, never sample across adjacent glyph atlas rectangles.
            if (HasEffects && EffectsSupported)
            {
                m_padding = Mathf.Max(m_padding, AtlasPadding(m_sharedMaterial));
                for (int i = 1; i < m_subTextObjects.Length; i++)
                    if (m_subTextObjects[i])
                        m_subTextObjects[i].padding = Mathf.Max(m_subTextObjects[i].padding,
                            AtlasPadding(m_subTextObjects[i].sharedMaterial));
            }
            base.GenerateTextMesh();
            SyncEffects();
        }

        public override void ClearMesh()
        {
            base.ClearMesh();
            meshCleared = true;
            ClearEffects();
        }

        public override void UpdateVertexData(TMP_VertexDataUpdateFlags flags)
        {
            base.UpdateVertexData(flags);
            meshCleared = false;
            SyncEffects();
        }

        public override void UpdateVertexData()
        {
            base.UpdateVertexData();
            meshCleared = false;
            SyncEffects();
        }

        public override void UpdateGeometry(Mesh mesh, int index)
        {
            base.UpdateGeometry(mesh, index);
            meshCleared = false;
            if (index < shadows.Count && shadows[index]) shadows[index].UseMesh(mesh);
            if (index < outlines.Count && outlines[index]) outlines[index].UseMesh(mesh);
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            if (effectRoot && transform.parent) effectRoot.SetParent(transform.parent, false);
            RefreshEffects();
        }

        private void SyncEffects()
        {
            if (syncing || !this) return;
            syncing = true;
            try
            {
                // Keep render-only clones in sync with animated material properties.
                if (faceSource && faceMaterial) FaceOnly(faceSource, ref faceMaterial, faceStencil);
                for (int i = 1; i < m_subTextObjects.Length; i++)
                {
                    var sub = m_subTextObjects[i];
                    if (!sub) continue;
                    var modifier = sub.GetComponent<SdfTextFaceMaterial>();
                    if (!modifier && EffectsSupported)
                    {
                        modifier = sub.gameObject.AddComponent<SdfTextFaceMaterial>();
                        modifier.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
                    }
                    if (modifier && modifier.Owner != this)
                    {
                        modifier.Owner = this;
                        sub.SetMaterialDirty();
                    }
                }

                bool visible = !meshCleared && isActiveAndEnabled && HasEffects && EffectsSupported && canvas
                    && transform.parent && textInfo != null && textInfo.characterCount > 0;
                if (!visible)
                {
                    ClearEffects();
                    return;
                }
                if (!effectShader) effectShader = Resources.Load<Shader>("SDFTextEffect");
                if (!effectShader) return;
                EnsureRoot();
                SyncTransform();
                effectRoot.gameObject.SetActive(true);

                int count = textInfo.materialCount;
                while (shadows.Count < count)
                {
                    shadows.Add(CreateLayer("Shadow"));
                    outlines.Add(CreateLayer("Outline"));
                }
                for (int i = 0; i < shadows.Count; i++)
                {
                    shadows[i].transform.SetSiblingIndex(i);
                    outlines[i].transform.SetSiblingIndex(shadows.Count + i);
                    var info = i < count ? textInfo.meshInfo[i] : default;
                    Material source = i == 0 ? fontSharedMaterial
                        : i < m_subTextObjects.Length && m_subTextObjects[i] ? m_subTextObjects[i].sharedMaterial : null;
                    bool active = i < count && info.vertexCount > 0 && IsDistanceField(source);
                    // Match the actual face renderer, including a mesh supplied via UpdateGeometry.
                    Mesh renderedMesh = i == 0 ? canvasRenderer.GetMesh()
                        : i < m_subTextObjects.Length && m_subTextObjects[i] ? m_subTextObjects[i].canvasRenderer.GetMesh() : null;
                    shadows[i].Configure(this, renderedMesh, source, effectShader, true,
                        active && sdfShadowEnabled && sdfShadowColor.a > 0);
                    outlines[i].Configure(this, renderedMesh, source, effectShader, false,
                        active && sdfOutlineEnabled && sdfOutlineWidth > 0 && sdfOutlineColor.a > 0);
                }
                // Fallback layers may first appear after the Canvas clipping phase.
                if (CanvasUpdateRegistry.IsRebuildingGraphics())
                {
                    effectRoot.GetComponentsInParent(false, clipMasks);
                    foreach (var mask in clipMasks) if (mask.isActiveAndEnabled) mask.PerformClipping();
                }
            }
            finally { syncing = false; }
        }

        private void ClearEffects()
        {
            // TMP may clear text during a Canvas rebuild. Unbinding meshes is safe there;
            // disabling Graphics would unregister them from the active rebuild queue.
            foreach (var layer in shadows) if (layer) layer.Clear();
            foreach (var layer in outlines) if (layer) layer.Clear();
        }

        private void EnsureRoot()
        {
            if (effectRoot) return;
            var root = new GameObject("SDF Text Effects", typeof(RectTransform), typeof(LayoutElement), typeof(CanvasGroup));
            root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            effectRoot = (RectTransform)root.transform;
            effectRoot.SetParent(transform.parent, false);
            root.GetComponent<LayoutElement>().ignoreLayout = true;
            effectGroup = root.GetComponent<CanvasGroup>();
            effectGroup.blocksRaycasts = false;
            effectGroup.interactable = false;
            // The sibling may have been destroyed together with an old parent.
            shadows.Clear();
            outlines.Clear();
        }

        private SdfTextLayer CreateLayer(string layerName)
        {
            var child = new GameObject(layerName, typeof(RectTransform), typeof(CanvasRenderer));
            child.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            child.transform.SetParent(effectRoot, false);
            return child.AddComponent<SdfTextLayer>();
        }

        private void SyncTransform()
        {
            if (effectRoot.parent != transform.parent) effectRoot.SetParent(transform.parent, false);
            var source = rectTransform;
            effectRoot.anchorMin = source.anchorMin;
            effectRoot.anchorMax = source.anchorMax;
            effectRoot.pivot = source.pivot;
            effectRoot.sizeDelta = source.sizeDelta;
            effectRoot.anchoredPosition3D = source.anchoredPosition3D;
            effectRoot.localRotation = source.localRotation;
            effectRoot.localScale = source.localScale;
            effectRoot.gameObject.layer = gameObject.layer;
            int sourceIndex = transform.GetSiblingIndex(), rootIndex = effectRoot.GetSiblingIndex();
            int targetIndex = rootIndex < sourceIndex ? sourceIndex - 1 : sourceIndex;
            if (rootIndex != targetIndex) effectRoot.SetSiblingIndex(targetIndex);

            GetComponents(ownGroups);
            float alpha = 1;
            bool ignoreParents = false;
            foreach (var group in ownGroups)
                if (group.isActiveAndEnabled) { alpha *= group.alpha; ignoreParents |= group.ignoreParentGroups; }
            effectGroup.alpha = alpha;
            effectGroup.ignoreParentGroups = ignoreParents;
        }

        internal static bool IsDistanceField(Material value) => value && value.HasProperty("_GradientScale")
            && value.HasProperty("_WeightNormal") && value.HasProperty("_MainTex");

        private static float AtlasPadding(Material value) => IsDistanceField(value)
            ? Mathf.Max(0, value.GetFloat("_GradientScale") - 1) : 0;

        internal static Material FaceOnly(Material source, ref Material instance, Material stencil = null)
        {
            if (!instance || instance.shader != source.shader)
            {
                Release(instance);
                instance = new Material(source) { name = "SDF Text Face", hideFlags = HideFlags.HideAndDontSave };
            }
            CopyRenderProperties(source, instance, stencil);
            instance.SetFloat("_OutlineWidth", 0);
            instance.SetFloat("_OutlineSoftness", 0);
            instance.DisableKeyword("OUTLINE_ON");
            instance.DisableKeyword("UNDERLAY_ON");
            instance.DisableKeyword("UNDERLAY_INNER");
            instance.DisableKeyword("GLOW_ON");
            return instance;
        }

        // uGUI caches stencil variants by material identity. Refresh their style/atlas while
        // retaining stencil state so material animation remains correct inside a Mask.
        internal static void CopyRenderProperties(Material source, Material destination, Material stencil)
        {
            if (!stencil || stencil == source) { destination.CopyPropertiesFromMaterial(source); return; }
            int comparison = stencil.GetInt("_StencilComp"), reference = stencil.GetInt("_Stencil");
            int operation = stencil.GetInt("_StencilOp"), read = stencil.GetInt("_StencilReadMask");
            int write = stencil.GetInt("_StencilWriteMask"), colorMask = stencil.GetInt("_ColorMask");
            bool alphaClip = stencil.IsKeywordEnabled("UNITY_UI_ALPHACLIP");
            destination.CopyPropertiesFromMaterial(source);
            destination.SetInt("_StencilComp", comparison);
            destination.SetInt("_Stencil", reference);
            destination.SetInt("_StencilOp", operation);
            destination.SetInt("_StencilReadMask", read);
            destination.SetInt("_StencilWriteMask", write);
            destination.SetInt("_ColorMask", colorMask);
            if (alphaClip) destination.EnableKeyword("UNITY_UI_ALPHACLIP");
        }

        internal static void Release(Object value)
        {
            if (!value) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;
        private static float Positive(float value) => Mathf.Max(0, Finite(value));
    }
}
