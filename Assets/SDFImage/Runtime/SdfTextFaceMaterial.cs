using UnityEngine;
using UnityEngine.UI;

namespace SDFUI
{
    /// <summary>Applies the same face-only material policy to TMP fallback/material submeshes.</summary>
    [ExecuteAlways, AddComponentMenu("")]
    public sealed class SdfTextFaceMaterial : MonoBehaviour, IMaterialModifier
    {
        public SdfText Owner { get; set; }
        private Material source, stencil, instance;

        public Material GetModifiedMaterial(Material baseMaterial)
        {
            if (!Owner || !Owner.isActiveAndEnabled || !Owner.EffectsSupported || !SdfText.IsDistanceField(baseMaterial))
                return baseMaterial;
            var subMesh = GetComponent<TMPro.TMP_SubMeshUI>();
            source = subMesh ? subMesh.sharedMaterial : baseMaterial;
            stencil = baseMaterial;
            return SdfText.FaceOnly(source, ref instance, stencil);
        }

        private void LateUpdate()
        {
            if (source && instance) SdfText.FaceOnly(source, ref instance, stencil);
        }

        private void OnDestroy() => SdfText.Release(instance);
    }
}
