// Colour pass of an erased object. See Erase.hlsl. Needs the URP Opaque Texture.
Shader "MoveIt/Erase Fill"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "EraseFill"
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend Off

            HLSLPROGRAM
            #pragma vertex EraseVert
            #pragma fragment EraseFillFrag
            #include "Assets/AR/Shaders/Erase.hlsl"
            ENDHLSL
        }
    }
}
