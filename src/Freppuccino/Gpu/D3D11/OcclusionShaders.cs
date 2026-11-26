namespace Freppuccino.Gpu.D3D11;

static class OcclusionShaders
{
    const string Common = """
        static const float AO_RADIUS = 0.12;
        static const int AO_SAMPLES = 16;
        static const float AO_STRENGTH = 0.85;
        static const float AO_BIAS = 0.03;
        static const int BLUR_RADIUS = 6;
        static const float BLUR_SIGMA = 3.5;
        static const float BLUR_DEPTH = 0.02;

        cbuffer Frame : register(b0) { float4 camPos, camX, camY, camZ, frustum, params, bound; };
        cbuffer Post : register(b1) { float4 size; float4 dir; };
        Texture2D<float4> gPos : register(t0);
        Texture2D<float4> gNrm : register(t1);
        Texture2D<float4> gColor : register(t2);
        Texture2D<float> gAo : register(t3);
        SamplerState pointS : register(s0);
        SamplerState linearS : register(s1);

        float Depth(float3 q) { return dot(q - camPos.xyz, camZ.xyz); }

        float2 Project(float3 q)
        {
            float3 v = q - camPos.xyz;
            float x = dot(v, camX.xyz), y = dot(v, camY.xyz);
            if (camPos.w > 0.5) { float s = camZ.w / max(dot(v, camZ.xyz), 1e-9); x *= s; y *= s; }
            return float2((x - frustum.x) / (frustum.y - frustum.x), 1.0 - (y - frustum.z) / (frustum.w - frustum.z));
        }
        """;

    public const string Vertex = """
        float4 main(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
        }
        """;

    public const string Occlusion = Common + """

        float main(float4 sv : SV_Position) : SV_Target
        {
            int2 c = min(int2(sv.xy) * 2, int2(size.xy) - 1);
            float4 P = gPos.Load(int3(c, 0));
            if (P.w <= 0.0) return 1.0;
            float3 n = normalize(gNrm.Load(int3(c, 0)).xyz);
            float3 t = normalize(cross(n, abs(n.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0)));
            float3 b = cross(n, t);
            float spin = 6.2831853 * frac(52.9829189 * frac(dot(sv.xy, float2(0.06711056, 0.00583715))));
            float R = AO_RADIUS * bound.w;
            float dP = Depth(P.xyz);
            float occ = 0.0;
            [loop] for (int k = 0; k < AO_SAMPLES; k++)
            {
                float fk = (k + 0.5) / AO_SAMPLES;
                float cosT = sqrt(1.0 - fk), sinT = sqrt(fk);
                float a = k * 2.3999632 + spin;
                float rk = frac(k * 0.618034 + 0.31);
                float r = R * lerp(0.08, 1.0, rk * rk);
                float3 d = (t * cos(a) + b * sin(a)) * sinT + n * cosT;
                float3 q = P.xyz + d * r;
                float2 uv = Project(q);
                if (any(uv < 0.0) || any(uv > 1.0)) continue;
                float4 S = gPos.SampleLevel(pointS, uv, 0);
                if (S.w == 0.0 || S.w < -1.5) continue;
                float dS = Depth(S.xyz), dQ = Depth(q);
                if (dS < dQ - AO_BIAS * r) occ += saturate(r / max(abs(dP - dS), 1e-9));
            }
            return 1.0 - occ / AO_SAMPLES;
        }
        """;

    public const string Blur = Common + """

        float main(float4 sv : SV_Position) : SV_Target
        {
            int2 h = int2(sv.xy);
            int2 hmax = int2(size.zw) - 1;
            int2 c = min(h * 2, int2(size.xy) - 1);
            float4 P = gPos.Load(int3(c, 0));
            float a0 = gAo.Load(int3(h, 0));
            if (P.w <= 0.0) return a0;
            float d0 = Depth(P.xyz);
            float3 n0 = gNrm.Load(int3(c, 0)).xyz;
            float sum = 0.0, wsum = 0.0;
            [loop] for (int i = -BLUR_RADIUS; i <= BLUR_RADIUS; i++)
            {
                int2 hs = clamp(h + int2(dir.xy) * i, int2(0, 0), hmax);
                int2 cs = min(hs * 2, int2(size.xy) - 1);
                float4 S = gPos.Load(int3(cs, 0));
                if (S.w <= 0.0) continue;
                float w = exp(-0.5 * i * i / (BLUR_SIGMA * BLUR_SIGMA));
                w *= exp(-abs(Depth(S.xyz) - d0) / (BLUR_DEPTH * max(abs(d0), 1e-9)));
                w *= pow(saturate(dot(gNrm.Load(int3(cs, 0)).xyz, n0)), 4.0);
                sum += w * gAo.Load(int3(hs, 0));
                wsum += w;
            }
            return wsum > 0.0 ? sum / wsum : a0;
        }
        """;

    public const string Composite = Common + """

        float4 main(float4 sv : SV_Position) : SV_Target
        {
            int2 c = int2(sv.xy - dir.zw);
            float4 col = gColor.Load(int3(c, 0));
            if (col.a <= 0.0) discard;
            float4 P = gPos.Load(int3(c, 0));
            if (P.w <= 0.0) return float4(col.rgb, 1.0);
            float ao = gAo.SampleLevel(linearS, (float2(c) + 0.5) / size.xy, 0);
            return float4(col.rgb * (1.0 - AO_STRENGTH * (1.0 - ao)), 1.0);
        }
        """;
}
