namespace Freppuccino.Gpu.D3D11;

static class OcclusionShaders
{
    const string Common = """
        static const float AO_RADIUS = 0.12;
        static const float AO_MIN_RADIUS = 0.35;
        static const int AO_SAMPLES = 12;
        static const float AO_STRENGTH = 0.4;
        static const float AO_WIDE_RADIUS = 0.55;
        static const int AO_WIDE_SAMPLES = 12;
        static const float AO_WIDE_STRENGTH = 0.45;
        static const float AO_BIAS = 0.03;
        static const float AO_GRAZING = 0.5;
        static const int BLUR_RADIUS = 6;
        static const float BLUR_SIGMA = 3.5;
        static const float BLUR_DEPTH = 0.02;
        static const float BLUR_NORMAL = 2.0;
        static const float BLUR_NORMAL_FLOOR = 0.25;

        cbuffer Frame : register(b0) { float4 camPos, camX, camY, camZ, frustum, params, bound; };
        cbuffer Post : register(b1) { float4 size; float4 dir; float4 clipX, clipY, clipZ, clipW; float4 depth; float4 mode; float4 ink; };
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

        float Term(float3 P, float3 n, float3 t, float3 b, float dP, float spin, float R, int count, float phase)
        {
            float occ = 0.0;
            [loop] for (int k = 0; k < count; k++)
            {
                float fk = (k + 0.5) / count;
                float cosT = sqrt(1.0 - fk), sinT = sqrt(fk);
                float a = k * 2.3999632 + spin + phase;
                float rk = frac(k * 0.618034 + 0.31 + phase);
                float r = R * lerp(AO_MIN_RADIUS, 1.0, rk);
                float3 d = (t * cos(a) + b * sin(a)) * sinT + n * cosT;
                float3 q = P + d * r;
                float2 uv = Project(q);
                if (any(uv < 0.0) || any(uv > 1.0)) continue;
                float4 S = gPos.SampleLevel(pointS, uv, 0);
                if (S.w == 0.0 || S.w < -1.5) continue;
                float dS = Depth(S.xyz), dQ = Depth(q);
                if (dS < dQ - AO_BIAS * r) occ += saturate(r / max(abs(dP - dS), 1e-9));
            }
            return occ / count;
        }

        float main(float4 sv : SV_Position) : SV_Target
        {
            int2 c = min(int2(sv.xy) * 2, int2(size.xy) - 1);
            float4 P = gPos.Load(int3(c, 0));
            if (P.w <= 0.0) return 1.0;
            float3 n = normalize(gNrm.Load(int3(c, 0)).xyz);
            float3 t = normalize(cross(n, abs(n.z) < 0.9 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0)));
            float3 b = cross(n, t);
            float spin = 6.2831853 * frac(52.9829189 * frac(dot(sv.xy, float2(0.06711056, 0.00583715))));
            float dP = Depth(P.xyz);
            float fine = Term(P.xyz, n, t, b, dP, spin, AO_RADIUS * bound.w, AO_SAMPLES, 0.0);
            float wide = Term(P.xyz, n, t, b, dP, spin, AO_WIDE_RADIUS * bound.w, AO_WIDE_SAMPLES, 0.5);
            float facing = smoothstep(0.0, AO_GRAZING, abs(dot(n, camZ.xyz)));
            return saturate(1.0 - facing * (AO_STRENGTH * fine + AO_WIDE_STRENGTH * wide));
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
                w *= lerp(BLUR_NORMAL_FLOOR, 1.0, pow(saturate(dot(gNrm.Load(int3(cs, 0)).xyz, n0)), BLUR_NORMAL));
                sum += w * gAo.Load(int3(hs, 0));
                wsum += w;
            }
            return wsum > 0.0 ? sum / wsum : a0;
        }
        """;

    public const string OutlineVertex = Common + """

        StructuredBuffer<float4> segs : register(t4);
        struct V { float4 pos : SV_Position; float3 world : WORLD; float side : SIDE; };

        float4 Clip(float3 p) { float4 q = float4(p, 1.0); return float4(dot(clipX, q), dot(clipY, q), dot(clipZ, q), dot(clipW, q)); }

        V main(uint id : SV_VertexID)
        {
            uint s = id / 6, c = id % 6;
            float3 a = segs[s * 2].xyz, b = segs[s * 2 + 1].xyz;
            bool atB = c == 1 || c == 2 || c == 4;
            float sd = (c == 2 || c == 4 || c == 5) ? 1.0 : -1.0;
            float4 ca = Clip(a), cb = Clip(b);
            V o;
            o.world = atB ? b : a;
            o.side = sd * (mode.y + 1.0);
            float wMin = max(1e-4, 1e-4 * max(abs(ca.w), abs(cb.w)));
            if (ca.w < wMin && cb.w < wMin) { o.pos = float4(0.0, 0.0, 2.0, 1.0); return o; }
            if (ca.w < wMin) { a = lerp(a, b, (wMin - ca.w) / (cb.w - ca.w)); ca = Clip(a); }
            if (cb.w < wMin) { b = lerp(a, b, (wMin - ca.w) / (cb.w - ca.w)); cb = Clip(b); }
            o.world = atB ? b : a;
            float2 half2 = size.xy * 0.5;
            float2 d = (cb.xy / cb.w - ca.xy / ca.w) * half2;
            float len = max(length(d), 1e-6);
            float2 t = d / len, n = float2(-t.y, t.x);
            float4 cp = atB ? cb : ca;
            float2 off = n * sd * (mode.y + 1.0) / half2;
            o.pos = float4(cp.xy + off * cp.w, 0.5 * cp.w, cp.w);
            return o;
        }
        """;

    const string OutlineBody = """

        StructuredBuffer<float4> segs : register(t4);
        struct V { float4 pos : SV_Position; float3 world : WORLD; float side : SIDE; };

        static const int ADDED_REACH = 3;
        static const float ADDED_TOL = 2.5;
        static const float ADDED_SPAN = 64.0;
        static const int ADDED_BEHIND = 2;

        float DepthOf(float3 p)
        {
            float4 q = float4(p, 1.0);
            return lerp(depth.x, depth.y, saturate(0.5 - 0.5 * dot(clipZ, q) / dot(clipW, q)));
        }

        bool Surface(float4 S) { return S.w > 0.0 || S.w == -1.0; }

        bool OnOrInside(float3 p, float foot, bool persp)
        {
            float4 q = float4(p, 1.0);
            float w = dot(clipW, q);
            int2 cc = int2(floor(float2(0.5 + 0.5 * dot(clipX, q) / w, 0.5 - 0.5 * dot(clipY, q) / w) * size.xy));
            float3 view = persp ? normalize(p - camPos.xyz) : camZ.xyz;
            float tol = ADDED_TOL * foot;
            int2 hi = int2(size.xy) - 1;
            [loop] for (int dy = -ADDED_REACH; dy <= ADDED_REACH; dy++)
            [loop] for (int dx = -ADDED_REACH; dx <= ADDED_REACH; dx++)
            {
                int2 c = cc + int2(dx, dy);
                if (any(c < 0) || any(c > hi)) continue;
                float4 S = gPos.Load(int3(c, 0));
                if (!Surface(S)) continue;
                float3 d = p - S.xyz;
                if (max(abs(dx), abs(dy)) <= ADDED_BEHIND && dot(d, view) > tol) return true;
                float3 n = gNrm.Load(int3(c, 0)).xyz;
                if (abs(dot(d, n)) < tol && length(d) < ADDED_SPAN * foot) return true;
            }
            return false;
        }

        float4 main(V i) : SV_Target
        {
            float cov = saturate(mode.y + 0.5 - abs(i.side));
            if (cov <= 0.0) discard;
            int2 px = int2(i.pos.xy);
            int2 c = int2(i.pos.xy - dir.zw);
            float3 p = i.world;
            float3 v = p - camPos.xyz;
            bool persp = camPos.w > 0.5;
            float foot = (frustum.y - frustum.x) / size.x * (persp ? dot(v, camZ.xyz) / camZ.w : 1.0);
            p -= (persp ? normalize(v) : camZ.xyz) * mode.x * foot;
            float lineD = DepthOf(p);
            float4 P = gPos.Load(int3(c, 0));
            float frepD = (P.w > 0.0 || P.w == -1.0) ? DepthOf(P.xyz) : 2.0;
            float rhinoD = RhinoDepth(px);
            if (mode.w > 0.5)
            {
                if (rhinoD < lineD - depth.z || OnOrInside(i.world, foot, persp)) discard;
                return float4(ink.rgb, cov);
            }
            if (rhinoD < lineD - depth.z && rhinoD < frepD - depth.z) discard;
            float a = frepD < lineD ? mode.z : 1.0;
            if (a <= 0.0) discard;
            return float4(ink.rgb, a * cov);
        }
        """;

    public const string OutlinePixelMs = Common + """

        Texture2DMS<float> rhinoDepth : register(t5);
        float RhinoDepth(int2 px) { return rhinoDepth.Load(px, 0); }
        """ + OutlineBody;

    public const string OutlinePixel1 = Common + """

        Texture2D<float> rhinoDepth : register(t5);
        float RhinoDepth(int2 px) { return rhinoDepth.Load(int3(px, 0)); }
        """ + OutlineBody;

    public const string Composite = Common + """

        float SurfaceDepth(float3 p)
        {
            float3 v = p - camPos.xyz;
            bool persp = camPos.w > 0.5;
            float foot = (frustum.y - frustum.x) / size.x * (persp ? dot(v, camZ.xyz) / camZ.w : 1.0);
            float4 q = float4(p + (persp ? normalize(v) : camZ.xyz) * mode.z * foot, 1.0);
            return lerp(depth.x, depth.y, saturate(0.5 - 0.5 * dot(clipZ, q) / dot(clipW, q)));
        }

        float4 main(float4 sv : SV_Position, out float z : SV_Depth) : SV_Target
        {
            int2 c = int2(sv.xy - dir.zw);
            float4 col = gColor.Load(int3(c, 0));
            if (col.a <= 0.0) discard;
            float4 P = gPos.Load(int3(c, 0));
            bool band = P.w < -1.5 || P.w == 0.0;
            if (band != (mode.x > 0.5)) discard;
            z = band ? depth.y : SurfaceDepth(P.xyz);
            if (P.w <= 0.0 || mode.y < 0.5) return float4(col.rgb, 1.0);
            float d0 = Depth(P.xyz);
            float3 n0 = gNrm.Load(int3(c, 0)).xyz;
            float2 hp = (float2(c) + 0.5) * 0.5 - 0.5;
            int2 h0 = int2(floor(hp));
            float2 f = hp - float2(h0);
            int2 hmax = int2(size.zw) - 1;
            float sum = 0.0, wsum = 0.0;
            [unroll] for (int j = 0; j < 4; j++)
            {
                int2 o = int2(j & 1, j >> 1);
                int2 hs = clamp(h0 + o, int2(0, 0), hmax);
                int2 cs = min(hs * 2, int2(size.xy) - 1);
                float4 S = gPos.Load(int3(cs, 0));
                if (S.w <= 0.0) continue;
                float2 b = lerp(1.0 - f, f, float2(o));
                float w = b.x * b.y + 1e-4;
                w *= exp(-abs(Depth(S.xyz) - d0) / (BLUR_DEPTH * max(abs(d0), 1e-9)));
                w *= lerp(BLUR_NORMAL_FLOOR, 1.0, pow(saturate(dot(gNrm.Load(int3(cs, 0)).xyz, n0)), BLUR_NORMAL));
                sum += w * gAo.Load(int3(hs, 0));
                wsum += w;
            }
            float ao = wsum > 1e-6 ? sum / wsum : 1.0;
            return float4(col.rgb * ao, 1.0);
        }
        """;
}
