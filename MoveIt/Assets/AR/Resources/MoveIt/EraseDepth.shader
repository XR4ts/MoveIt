// Depth pass of an erased object. See Erase.hlsl.
Shader "MoveIt/Erase Depth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry-1" }

        Pass
        {
            Name "EraseDepth"
            Cull Front
            ZWrite On
            ZTest Always
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex EraseVert
            #pragma fragment EraseDepthFrag
            #include "Assets/AR/Shaders/Erase.hlsl"
            ENDHLSL
        }
    }
}
