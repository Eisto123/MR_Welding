Shader "Custom/MeshMelt2_URP"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _LavaTex ("Lava Texture", 2D) = "white" {}
        _Amount ("Extrusion Amount", Range(-1,1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags {"LightMode" = "UniversalForward"}

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 customColor : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_LavaTex);
            SAMPLER(sampler_LavaTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _LavaTex_ST;
                float _Amount;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                // Apply vertex displacement based on vertex color alpha and normal
                float3 positionOS = input.positionOS.xyz;
                positionOS += input.normalOS * input.color.a * _Amount;

                output.positionHCS = TransformObjectToHClip(positionOS);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.customColor = input.color;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionWS = TransformObjectToWorld(positionOS);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // Sample main texture
                half3 mainColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;

                // Calculate scrolling UVs for lava texture
                float2 scrollUV = input.uv + float2(_Time.x * 0.032f * (1 - input.customColor.a), _Time.y * 0.02f * (1 - input.customColor.a));
                half3 lavaColor = SAMPLE_TEXTURE2D(_LavaTex, sampler_LavaTex, scrollUV).rgb;

                // Interpolate between lava and main texture based on vertex color alpha
                half3 finalColor = lerp(lavaColor, mainColor, input.customColor.a);

                // Add emission based on vertex color alpha (inverse relationship)
                half3 emission = finalColor * (1 - input.customColor.a);

                // Simple lighting calculation
                Light mainLight = GetMainLight();
                float3 normalWS = normalize(input.normalWS);
                float NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = mainLight.color * NdotL;

                finalColor = finalColor * (lighting + 0.2) + emission; // Add some ambient

                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }
    
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
