using ComputeSharp;
using ComputeSharp.D2D1;

namespace HeroCarousel.Shaders;

[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct LeftColorWashShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float3 baseColor;
    private readonly float3 accentColor;
    private readonly float strength;
    private readonly float warmth;

    public LeftColorWashShader(
        float width,
        float height,
        float baseRed,
        float baseGreen,
        float baseBlue,
        float accentRed,
        float accentGreen,
        float accentBlue,
        float strength,
        float warmth)
    {
        this.width = width;
        this.height = height;
        this.baseColor = new float3(baseRed, baseGreen, baseBlue);
        this.accentColor = new float3(accentRed, accentGreen, accentBlue);
        this.strength = strength;
        this.warmth = warmth;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 uv = position / new float2(width, height);

        float leftFalloff = 1.0f - Hlsl.SmoothStep(0.0f, 0.76f, uv.X);
        float longTail = 1.0f - Hlsl.SmoothStep(0.14f, 1.0f, uv.X);
        float verticalBody = 0.58f + 0.42f * (1.0f - Hlsl.SmoothStep(0.0f, 0.82f, Hlsl.Abs(uv.Y - 0.50f)));
        float leftRim = 1.0f - Hlsl.SmoothStep(0.0f, 0.26f, uv.X);
        float topRim = 1.0f - Hlsl.SmoothStep(0.0f, 0.20f, uv.Y);
        float bottomRim = Hlsl.SmoothStep(0.70f, 1.0f, uv.Y);
        float edgeRim = leftRim * 0.88f + (topRim + bottomRim) * leftFalloff * 0.30f;

        float2 upperField = (uv - new float2(0.08f, 0.23f)) * new float2(1.35f, 1.85f);
        float2 lowerField = (uv - new float2(0.03f, 0.82f)) * new float2(1.12f, 1.55f);
        float upperLift = 1.0f - Hlsl.SmoothStep(0.0f, 0.72f, Hlsl.Length(upperField));
        float lowerLift = 1.0f - Hlsl.SmoothStep(0.0f, 0.84f, Hlsl.Length(lowerField));

        float grain = Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(position, new float2(27.619f, 57.583f))) * 43758.5453f);
        float alpha = Hlsl.Saturate(
            (leftFalloff * 0.82f + longTail * 0.34f + upperLift * 0.26f + lowerLift * 0.22f + edgeRim) *
            verticalBody *
            (0.965f + grain * 0.07f) *
            strength);

        float blend = Hlsl.Saturate(warmth * (0.35f + uv.Y * 0.45f) + upperLift * 0.16f);
        float3 color = Hlsl.Lerp(baseColor, accentColor, blend);

        return new float4(color * alpha, alpha);
    }
}
