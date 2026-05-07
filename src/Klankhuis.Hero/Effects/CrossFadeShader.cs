using ComputeSharp;
using ComputeSharp.D2D1;

namespace Klankhuis.Hero.Effects;

// Vertical per-pixel cross-fade shader (verbatim from the Microsoft Store
// ComputeSharp case study §4.2.1). Blends two inputs along the Y axis with
// a sin-based ease-out so the fade between source and reflection is
// perceptually smooth.
[D2DInputCount(2)]
[D2DInputSimple(0)]
[D2DInputSimple(1)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DInputDescription(1, D2D1Filter.MinMagMipPoint)]
[D2DPixelOptions(D2D1PixelOptions.TrivialSampling)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct CrossFadeShader : ID2D1PixelShader
{
    private readonly int _offsetStartY;
    private readonly int _offsetLengthY;

    public CrossFadeShader(int offsetStartY, int offsetLengthY)
    {
        _offsetStartY = offsetStartY;
        _offsetLengthY = offsetLengthY;
    }

    public float4 Execute()
    {
        // Vertical position within the output image
        int offsetY = (int)D2D.GetScenePosition().Y;

        // Linear blend factor across the configured fade length
        float factor = Hlsl.Saturate((offsetY - _offsetStartY) / (float)_offsetLengthY);

        // sin(t * pi/2) — fast first half, gentle landing. Coefficient 1.57
        // is arbitrary, picked through iteration in the original case study.
        float easing = Hlsl.Sin(factor * 1.57f);

        // Blend RGB; alpha forced to 1
        float3 blend = Hlsl.Lerp(D2D.GetInput(0).XYZ, D2D.GetInput(1).XYZ, easing);

        return new float4(blend, 1f);
    }
}
