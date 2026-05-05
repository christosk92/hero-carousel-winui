using ComputeSharp;
using ComputeSharp.D2D1;

namespace HeroCarousel.Shaders;

[D2DGeneratedPixelShaderDescriptor]
[D2DInputCount(0)]
[D2DOutputBuffer(D2D1BufferPrecision.UInt8Normalized)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.EnableLinking | D2D1CompileOptions.PartialPrecision)]
public readonly partial struct HeroGlowShader : ID2D1PixelShader
{
    private readonly float width;
    private readonly float height;
    private readonly float3 color;
    private readonly float strength;

    public HeroGlowShader(float width, float height, float red, float green, float blue, float strength)
    {
        this.width = width;
        this.height = height;
        this.color = new float3(red, green, blue);
        this.strength = strength;
    }

    public float4 Execute()
    {
        float2 position = D2D.GetScenePosition().XY;
        float2 center = new(width * 0.5f, height * 0.5f);
        float2 stageHalf = new((width - 150.0f) * 0.5f, (height - 150.0f) * 0.5f);
        float radius = 100.0f;
        float2 delta = Hlsl.Abs(position - center);
        float2 q = delta - (stageHalf - new float2(radius, radius));
        float outside = Hlsl.Length(Hlsl.Max(q, new float2(0.0f, 0.0f)));
        float inside = Hlsl.Min(Hlsl.Max(q.X, q.Y), 0.0f);
        float signedDistance = outside + inside - radius;

        float outsideDistance = Hlsl.Max(signedDistance, 0.0f);
        float alpha = Hlsl.Saturate((1.0f - Hlsl.SmoothStep(0.0f, 130.0f, outsideDistance)) * strength * 0.20f);

        return new float4(color * alpha, alpha);
    }
}
