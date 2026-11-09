// Copies one of Unity's render textures into an OpenXR swapchain image (whole image, one triangle).
// Unity renders into its textures upside down on Direct3D, so the copy can flip the picture.
// The source is read and the target written through sRGB views, so the colours pass through unchanged.

cbuffer Params : register(b0)
{
    float4 uvScaleOffset;   // uv = xy * uv + zw
};

Texture2D src : register(t0);
SamplerState smp : register(s0);

struct V2P
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

V2P VS(uint id : SV_VertexID)
{
    // A triangle that covers the whole target: (0,0) (2,0) (0,2) in uv.
    float2 uv = float2((id << 1) & 2, id & 2);
    V2P o;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.uv = uv;
    return o;
}

float4 PS(V2P i) : SV_Target
{
    float4 c = src.Sample(smp, i.uv * uvScaleOffset.xy + uvScaleOffset.zw);
    return float4(c.rgb, 1);
}
