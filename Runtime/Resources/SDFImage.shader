Shader "UI/SDF Image"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _SdfTex ("Signed Distance (Source Pixels)", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _HasSprite ("Valid Sprite", Float) = 0
        _SourceSize ("Source Size, Padding, Range", Vector) = (1,1,0,1)
        _ImageRect ("Local Image Rect", Vector) = (0,0,1,1)
        _SourceBorder ("Source Border", Vector) = (0,0,0,0)
        _LocalBorder ("Local Border", Vector) = (0,0,0,0)
        _Outline ("Width, Softness, Position", Vector) = (0,0,0,0)
        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _OutlineTextureColor ("Use Texture Color, Intensity", Vector) = (0,1,0,0)
        _Shadow ("Offset, Blur, Spread", Vector) = (0,0,0,0)
        _ShadowColor ("Shadow Color", Color) = (0,0,0,0)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "SDF UI"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 localPosition : TEXCOORD0;
                half4 mask : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            sampler2D _SdfTex;
            float4 _SourceSize, _ImageRect, _SourceBorder, _LocalBorder, _Outline, _OutlineTextureColor, _Shadow;
            fixed4 _Color, _OutlineColor, _ShadowColor;
            float4 _ClipRect;
            float _UIMaskSoftnessX, _UIMaskSoftnessY, _HasSprite;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.localPosition = v.texcoord;
                o.color = v.color * _Color;
                float2 pixelSize = o.vertex.w / abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));
                float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
                o.mask = half4(v.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                    0.25 / (0.25 * half2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize)));
                return o;
            }

            // Piecewise mapping also extrapolates the outer border into the padded SDF.
            // Return source position and the exact source-pixels-per-local-unit rate for this slice.
            float2 MapAxis(float p, float extent, float source, float low, float high, float srcLow, float srcHigh)
            {
                // When the center collapses, sample an adjacent border even exactly on the join.
                if (extent - low - high <= 0.0001)
                {
                    if (low > 0.0001 && p <= low) return float2(p * srcLow / low, srcLow / low);
                    if (high > 0.0001) return float2(source - (extent - p) * srcHigh / high, srcHigh / high);
                }
                if (low > 0.0001 && p < low) return float2(p * srcLow / low, srcLow / low);
                if (high > 0.0001 && p > extent - high) return float2(source - (extent - p) * srcHigh / high, srcHigh / high);
                float rate = max(0.0, source - srcLow - srcHigh) / max(0.0001, extent - low - high);
                return float2(srcLow + (p - low) * rate, rate);
            }

            float4 SourcePoint(float2 local)
            {
                float2 p = local - _ImageRect.xy;
                float2 x = MapAxis(p.x, _ImageRect.z, _SourceSize.x, _LocalBorder.x, _LocalBorder.z, _SourceBorder.x, _SourceBorder.z);
                float2 y = MapAxis(p.y, _ImageRect.w, _SourceSize.y, _LocalBorder.y, _LocalBorder.w, _SourceBorder.y, _SourceBorder.w);
                return float4(x.x, y.x, x.y, y.y);
            }

            float2 TextureUV(float2 source)
            {
                return (source + _SourceSize.z) / (_SourceSize.xy + 2 * _SourceSize.z);
            }

            float Domain(float2 source)
            {
                float2 lo = step(-_SourceSize.z + 0.5, source);
                float2 hi = step(source, _SourceSize.xy + _SourceSize.z - 0.5);
                return lo.x * lo.y * hi.x * hi.y;
            }

            // Recover the distance gradient in Canvas local coordinates. This preserves thickness
            // under nonuniform image resizing and screen/camera projection without extra texture taps.
            float2 LocalDistance(float distance, float2 local, float2 sourceRate)
            {
                float2 dx = ddx(local), dy = ddy(local);
                float det = dx.x * dy.y - dx.y * dy.x;
                float dsx = ddx(distance), dsy = ddy(distance);
                float2 gradient = float2(dsx * dy.y - dsy * dx.y, dsy * dx.x - dsx * dy.x) / (abs(det) > 1e-10 ? det : 1e-10);
                // A unit SDF normal transformed by diag(sourceRate) lies between these singular values.
                // Equal rates give exact uniform scaling, avoiding width wobble from discrete SDF gradients.
                float minimumRate = max(1e-5, min(sourceRate.x, sourceRate.y));
                float maximumRate = max(minimumRate, max(sourceRate.x, sourceRate.y));
                float scale = clamp(length(gradient), minimumRate, maximumRate);
                return float2(distance / scale, max(0.001, (abs(dsx) + abs(dsy)) / scale * 0.5));
            }

            float Coverage(float distance, float aa, float softness)
            {
                float edge = max(0.001, aa + softness * 0.5);
                return smoothstep(-edge, edge, distance);
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 mapping = SourcePoint(i.localPosition);
                float2 source = mapping.xy;
                float2 uv = TextureUV(source);
                float rawDistance = tex2D(_SdfTex, uv).r;
                float2 sdf = LocalDistance(rawDistance, i.localPosition, mapping.zw);
                float domain = Domain(source) * _HasSprite;
                fixed4 fill = tex2D(_MainTex, uv);
                // The bake dilates source RGB into transparent padding. Reuse this untinted
                // sample before premultiplication; outline alpha still comes from Outline Color.
                float3 outlineRgb = _OutlineTextureColor.x > 0.5
                    ? fill.rgb * _OutlineTextureColor.y : _OutlineColor.rgb;
                fill.rgb *= i.color.rgb;
                fill.a *= domain;
                fill.rgb *= fill.a;

                float outerWidth = _Outline.z < 0.5 ? _Outline.x : (_Outline.z > 1.5 ? _Outline.x * 0.5 : 0);
                float innerWidth = _Outline.z > 1.5 ? _Outline.x * 0.5 : (_Outline.z > 0.5 ? _Outline.x : 0);
                float contour = Coverage(sdf.x, sdf.y, _Outline.y);
                float expanded = Coverage(sdf.x + outerWidth, sdf.y, _Outline.y);
                float outerCoverage = saturate((expanded - contour) / max(1 - contour, 0.0001));
                float innerRing = saturate(contour - Coverage(sdf.x - innerWidth, sdf.y, _Outline.y));
                // The binary SDF can be fully inside while the source's filtered edge is still translucent.
                // Let the outline cover that join across a two-texel filtering footprint, while preserving
                // authored transparency farther inside. No extra texture samples or rebake are needed.
                // Measure this footprint in source pixels so stretching one axis cannot fill distant transparency.
                float joinRadius = max(2.0, 0.5 * fwidth(rawDistance));
                float joinContour = min(contour, Coverage(rawDistance, joinRadius, 0));
                float joinedCoverage = saturate((expanded - joinContour) / max(1 - joinContour, 0.0001));
                outerCoverage = lerp(outerCoverage, joinedCoverage, saturate(outerWidth / max(sdf.y, 0.001)));
                float outerAlpha = (1 - fill.a) * outerCoverage * domain * _OutlineColor.a;
                float innerAlpha = min(innerRing * domain, fill.a) * _OutlineColor.a;
                float4 foreground = float4(outlineRgb * (outerAlpha + innerAlpha)
                    + fill.rgb * (1 - innerAlpha / max(fill.a, 0.0001)), fill.a + outerAlpha);

                // Shift in local units BEFORE remapping; simply offsetting UVs would distort sliced shadows.
                float4 shadowMapping = SourcePoint(i.localPosition - _Shadow.xy);
                float2 shadowSource = shadowMapping.xy;
                float shadowRaw = tex2D(_SdfTex, TextureUV(shadowSource)).r;
                float2 shadowSdf = LocalDistance(shadowRaw, i.localPosition, shadowMapping.zw);
                float shadowAlpha = Coverage(shadowSdf.x + _Shadow.w, shadowSdf.y, _Shadow.z) * _ShadowColor.a * Domain(shadowSource) * _HasSprite;
                float4 result = foreground + fixed4(_ShadowColor.rgb * shadowAlpha, shadowAlpha) * (1 - foreground.a);
                // Vertex/CanvasGroup alpha fades the composite exactly once.
                result *= i.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                half2 mask = saturate((_ClipRect.zw - _ClipRect.xy - abs(i.mask.xy)) * i.mask.zw);
                result *= mask.x * mask.y;
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(result.a - 0.001);
                #endif
                return result;
            }
            ENDCG
        }
    }
}
