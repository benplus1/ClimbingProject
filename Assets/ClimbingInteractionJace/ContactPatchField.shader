Shader "VHard/Contact Patch Field"
{
    Properties
    {
        _ContactThreshold ("Solid Contact Distance", Float) = 0.008
        _ProximityThreshold ("Patch Falloff Distance", Float) = 0.025
        _PatchExposure ("Patch Exposure", Range(0, 4)) = 1
        _ThumbColor ("Thumb", Color) = (0.9020, 0.6235, 0, 1)
        _IndexColor ("Index", Color) = (0.3373, 0.7059, 0.9137, 1)
        _MiddleColor ("Middle", Color) = (0, 0.6196, 0.4510, 1)
        _RingColor ("Ring", Color) = (0.8000, 0.4745, 0.6549, 1)
        _LittleColor ("Little", Color) = (0.8353, 0.3686, 0, 1)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+21"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "ContactPatchField"
            Tags { "LightMode" = "UniversalForward" }
            // Additive, so a vertex no fingertip reached adds exactly nothing and the scanned hold
            // reads through untouched; the patches are the surface lighting up, not paint over it.
            // Destination alpha is left alone: the glow must not accumulate into the coverage the
            // opaque pass already wrote.
            Blend One One, Zero One
            ZWrite Off
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // (min distance to the left hand, to the right hand, nearest fingertip ordinal, distance
            // to that fingertip), one entry per hold vertex, written by HandDistanceComputeShader and
            // bound per hold by GripHoldContactState. Ordinals 0-4 are the left hand thumb-to-little,
            // 5-9 the right, 10 none.
            StructuredBuffer<float4> _VertexContact;

            CBUFFER_START(UnityPerMaterial)
                float _ContactThreshold;
                float _ProximityThreshold;
                float _PatchExposure;
                float4 _ThumbColor;
                float4 _IndexColor;
                float4 _MiddleColor;
                float4 _RingColor;
                float4 _LittleColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 glow : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half3 FingerColor(int finger)
            {
                if (finger == 0) return _ThumbColor.rgb;
                if (finger == 1) return _IndexColor.rgb;
                if (finger == 2) return _MiddleColor.rgb;
                if (finger == 3) return _RingColor.rgb;
                return _LittleColor.rgb;
            }

            // Mirrors GripContactPatchPolicy.Intensity. A vertex the compute pass left unclaimed
            // carries a distance of float-max, which saturates to zero here without a branch.
            float PatchIntensity(float distanceToFingertip)
            {
                float span = max(_ProximityThreshold - _ContactThreshold, 0.000001);
                float t = saturate((distanceToFingertip - _ContactThreshold) / span);
                return 1.0 - (t * t * (3.0 - (2.0 * t)));
            }

            // The hue is resolved per vertex and the glow interpolated already multiplied by its
            // intensity: interpolating the ordinal instead would invent a finger that is not there
            // wherever two fingers' patches meet, and premultiplying is what the additive blend wants.
            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);

                float4 contact = _VertexContact[input.vertexID];
                int ordinal = (int)round(contact.z);
                float intensity = ordinal >= 0 && ordinal <= 9 ? PatchIntensity(contact.w) : 0.0;
                output.glow = FingerColor(ordinal >= 5 ? ordinal - 5 : ordinal) * intensity;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return half4(input.glow * _PatchExposure, 0);
            }
            ENDHLSL
        }
    }
}
