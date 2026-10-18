using System;

namespace BluePrinceNotes.UI;

/// <summary>
/// Procedural notebook paper for the notepad panel: cream page with grain, fibres, a soft vignette, a bound-edge
/// shade, faint stains, blue rules on the body baselines, a red margin rule, a red double rule under the header and
/// a drop shadow in a transparent border. Unity-free (plain managed code), so it can run on a worker thread; the
/// result is RGBA32 bytes with row 0 at the bottom, ready for Texture2D.LoadRawTextureData.
/// Colours are sRGB values (the game uses Gamma colour space, so UI blending happens on these values directly).
/// </summary>
internal static class NotebookPaper
{
    internal struct Geometry
    {
        public int PageW, PageH;     // page size in px (even, so the centred panel lands on whole pixels)
        public int Border;           // transparent border around the page for the shadow, px
        public float S;              // the notepad font size s, px
        public float MarginX;        // centre of the red margin rule, px from the page's left edge
        public float HeaderRuleY;    // centre of the upper (thicker) red header rule, px from the page top
        public float FirstRuleY;     // centre of the first blue rule, px from the page top
        public float RuleStep;       // body line pitch L, px (TMP line advance)
        public int RuleCount;        // number of blue rules
        public int Seed;
    }

    // ---------------------------------------------------------------- palette (sRGB 0-1)
    private static readonly Vec3 Paper = new(0.957f, 0.933f, 0.859f);       // fresh cream
    private static readonly Vec3 PaperAged = new(0.925f, 0.875f, 0.760f);   // mottled, yellowed patches
    private static readonly Vec3 EdgeTone = new(0.835f, 0.770f, 0.640f);    // burnt page edge
    private static readonly Vec3 FibreDark = new(0.560f, 0.505f, 0.420f);
    private static readonly Vec3 FibreLight = new(1.000f, 0.995f, 0.965f);
    private static readonly Vec3 StainTone = new(0.640f, 0.500f, 0.310f);
    private static readonly Vec3 RuleBlue = new(0.420f, 0.580f, 0.800f);    private const float RuleBlueA = 0.62f;
    private static readonly Vec3 MarginRed = new(0.860f, 0.330f, 0.360f);   private const float MarginRedA = 0.78f;
    private static readonly Vec3 HeaderRed = new(0.640f, 0.110f, 0.150f);   private const float HeaderRedA = 0.85f;
    private const float ShadowA = 0.55f;

    // ---------------------------------------------------------------- page

    public static byte[] Build(in Geometry g, out int texW, out int texH)
    {
        int W = g.PageW, H = g.PageH, B = g.Border;
        float s = g.S;
        texW = W + 2 * B;
        texH = H + 2 * B;
        var px = new byte[texW * texH * 4];
        var rnd = new Random(g.Seed);

        // Fibres: short random curves splatted into a page-size buffer (+ darker, - lighter).
        var fibre = new float[W * H];
        int fibres = (int)(W * (long)H / (s * s * 2.2f));  // sparse enough not to show through small text
        for (int i = 0; i < fibres; i++)
        {
            float x = (float)rnd.NextDouble() * W, y = (float)rnd.NextDouble() * H;
            float len = s * (0.25f + 0.85f * (float)rnd.NextDouble());
            float ang = (float)(rnd.NextDouble() * Math.PI * 2), bend = ((float)rnd.NextDouble() - 0.5f) * 2.5f / len;
            float amt = rnd.NextDouble() < 0.72 ? 0.05f + 0.07f * (float)rnd.NextDouble() : -(0.05f + 0.06f * (float)rnd.NextDouble());
            int steps = (int)(len * 2f);
            for (int k = 0; k < steps; k++)
            {
                float t = k / (float)steps;
                Splat(fibre, W, H, x, y, amt * MathF.Sin(t * MathF.PI));  // fades in and out along its length
                ang += bend * 0.5f;
                x += MathF.Cos(ang) * 0.5f;
                y += MathF.Sin(ang) * 0.5f;
            }
        }

        // Stains: one faint coffee ring partly off the lower right corner, and a small cluster of foxing dots.
        var stains = new (float x, float y, float r, float fill, float ring)[14];
        stains[0] = (W - s * (1.2f + (float)rnd.NextDouble()), H - s * (0.6f + (float)rnd.NextDouble()), s * 2.6f, 0.03f, 0.11f);
        float fx0 = W * (0.15f + 0.6f * (float)rnd.NextDouble()), fy0 = H * (0.08f + 0.15f * (float)rnd.NextDouble());
        for (int i = 1; i < stains.Length; i++)
        {
            float a = (float)(rnd.NextDouble() * Math.PI * 2), d = s * 2.2f * (float)Math.Sqrt(rnd.NextDouble());
            stains[i] = (fx0 + MathF.Cos(a) * d * 1.8f, fy0 + MathF.Sin(a) * d * 0.7f, s * (0.04f + 0.12f * (float)rnd.NextDouble()),
                0.10f + 0.12f * (float)rnd.NextDouble(), 0f);
        }

        // Per-row coverage of the horizontal rules, per-column coverage of the margin rule.
        float ruleT = MathF.Max(1f, 0.05f * s), headT1 = MathF.Max(1f, 0.055f * s), headT2 = MathF.Max(1f, 0.03f * s);
        float headGap = 0.13f * s, marginT = MathF.Max(1f, 0.055f * s);
        var rowRule = new float[H];
        var rowHead = new float[H];
        for (int y = 0; y < H; y++)
        {
            float c = 0f;
            for (int k = 0; k < g.RuleCount; k++) c = MathF.Max(c, Cover(y, g.FirstRuleY + k * g.RuleStep, ruleT));
            rowRule[y] = c;
            rowHead[y] = MathF.Max(Cover(y, g.HeaderRuleY, headT1), Cover(y, g.HeaderRuleY + headGap, headT2));
        }
        var colMargin = new float[W];
        for (int x = 0; x < W; x++) colMargin[x] = Cover(x, g.MarginX, marginT);

        float radius = 0.22f * s, shadowDy = 0.22f * s, seedF = g.Seed % 997;
        float hw = W * 0.5f, hh = H * 0.5f;

        // Mottling is low-frequency: sample it on a coarse grid and interpolate (saves ~40% of the time).
        const int Cell = 4;
        int mw = W / Cell + 2, mh = H / Cell + 2;
        var mottleGrid = new float[mw * mh];
        for (int gy = 0; gy < mh; gy++)
            for (int gx = 0; gx < mw; gx++)
                mottleGrid[gy * mw + gx] = Fbm(gx * Cell / (7f * s), gy * Cell / (7f * s), 3, g.Seed + 11);

        for (int ty = 0; ty < texH; ty++)
        {
            int row = (texH - 1 - ty) * texW * 4;   // Unity: row 0 is the bottom
            float py = ty - B + 0.5f;               // page coordinates, y down
            int iy = ty - B;
            bool inRows = iy >= 0 && iy < H;
            float rule = inRows ? rowRule[iy] : 0f, head = inRows ? rowHead[iy] : 0f;
            for (int tx = 0; tx < texW; tx++)
            {
                float pxf = tx - B + 0.5f;
                int ix = tx - B;
                int o = row + tx * 4;

                float sd = RoundRect(pxf - hw, py - hh, hw, hh, radius);
                float cover = Clamp01(0.5f - sd);
                float shadow = 0f;
                if (cover < 1f)
                {
                    float sds = RoundRect(pxf - hw, py - hh - shadowDy, hw - 0.1f * s, hh, radius);
                    shadow = ShadowA * MathF.Pow(Clamp01(1f - (sds + 0.15f * s) / (B * 1.05f)), 2.2f);
                    shadow = MathF.Max(shadow, 0.35f * Clamp01(1f - MathF.Max(0f, sd) / (0.12f * s)));  // contact shadow
                    if (cover <= 0f)
                    {
                        px[o] = px[o + 1] = px[o + 2] = 0;
                        px[o + 3] = ToByte(shadow);
                        continue;
                    }
                }

                // --- paper body
                float mx = Math.Clamp(pxf, 0f, W - 1f) / Cell, my = Math.Clamp(py, 0f, H - 1f) / Cell;
                int mx0 = (int)mx, my0 = (int)my;
                float mfx = mx - mx0, mfy = my - my0;
                int mi = my0 * mw + mx0;
                float mottle = Lerp(Lerp(mottleGrid[mi], mottleGrid[mi + 1], mfx), Lerp(mottleGrid[mi + mw], mottleGrid[mi + mw + 1], mfx), mfy);
                float fine = Fbm(pxf / (0.9f * s), py / (0.9f * s), 3, g.Seed + 23);
                float grain = Hash(tx, ty, g.Seed + 37);
                Vec3 c = Vec3.Lerp(Paper, PaperAged, 0.55f * SmoothStep(0.35f, 0.80f, mottle));
                c *= 1f + 0.035f * (fine - 0.5f) * 2f + 0.022f * (grain - 0.5f) * 2f;

                // vignette: broad radial darkening plus a burnt edge band
                float nx = (pxf - hw) / hw, ny = (py - hh) / hh;
                c *= 1f - 0.035f * (nx * nx + ny * ny);
                float edge = MathF.Min(MathF.Min(pxf, W - pxf), MathF.Min(py, H - py));
                if (edge < 1.4f * s)
                {
                    float wobble = 0.6f + 0.8f * Noise(pxf / (1.5f * s), py / (1.5f * s), g.Seed + 41);
                    c = Vec3.Lerp(c, EdgeTone, 0.55f * (1f - SmoothStep(0f, 0.9f * s * wobble, edge)));
                }
                // bound edge on the left
                if (pxf < 8f * s) c *= 1f - 0.07f * MathF.Exp(-pxf / (0.35f * s)) - 0.03f * MathF.Exp(-pxf / (1.6f * s));

                // stains
                for (int i = 0; i < stains.Length; i++)
                {
                    var st = stains[i];
                    float dx = pxf - st.x, dy = py - st.y;
                    float r2 = st.r * 1.3f;
                    if (dx * dx + dy * dy > r2 * r2) continue;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / st.r;
                    d += 0.12f * (Noise(pxf / (0.5f * s), py / (0.5f * s), g.Seed + 53 + i) - 0.5f);
                    float a = st.fill * (1f - SmoothStep(0.7f, 1f, d));
                    if (st.ring > 0f) a += st.ring * MathF.Exp(-(d - 1f) * (d - 1f) / 0.004f);
                    c = Vec3.Lerp(c, StainTone, a);
                }

                // fibres
                if (ix >= 0 && ix < W && inRows)
                {
                    float f = fibre[iy * W + ix];
                    if (f > 0f) c = Vec3.Lerp(c, FibreDark, MathF.Min(f, 0.22f));
                    else if (f < 0f) c = Vec3.Lerp(c, FibreLight, MathF.Min(-f, 0.22f));
                }

                // --- printed lines (slightly uneven, as printed on fibrous paper)
                if (rule > 0f || head > 0f)
                {
                    float ink = 0.78f + 0.22f * Noise(pxf / (0.35f * s), py * 0.37f + seedF, g.Seed + 61);
                    if (rule > 0f) c = Vec3.Lerp(c, RuleBlue, RuleBlueA * rule * ink);
                    if (head > 0f) c = Vec3.Lerp(c, HeaderRed, HeaderRedA * head * ink);
                }
                if (ix >= 0 && ix < W)
                {
                    float m = colMargin[ix];
                    if (m > 0f)
                    {
                        float ink = 0.78f + 0.22f * Noise(pxf * 0.37f + seedF, py / (0.35f * s), g.Seed + 67);
                        c = Vec3.Lerp(c, MarginRed, MarginRedA * m * ink);
                    }
                }

                // --- composite the page over its shadow (straight alpha)
                float outA = cover + shadow * (1f - cover);
                float k2 = cover / MathF.Max(outA, 1e-5f);
                px[o] = ToByte(c.X * k2);
                px[o + 1] = ToByte(c.Y * k2);
                px[o + 2] = ToByte(c.Z * k2);
                px[o + 3] = ToByte(outA);
            }
        }
        return px;
    }

    // ---------------------------------------------------------------- highlighter stroke (selection, list row)

    /// <summary>
    /// A yellow highlighter swipe, w x h, chisel-cut ends, streaky. Use as a Sliced sprite with a left/right border of
    /// h/2 so the ends keep their shape. Alpha is baked in (tint the Image white).
    /// </summary>
    public static byte[] BuildHighlighter(int w, int h, int seed)
    {
        var px = new byte[w * h * 4];
        var col = new Vec3(1.000f, 0.860f, 0.300f);
        const float MaxA = 0.70f;
        for (int ty = 0; ty < h; ty++)
        {
            int row = (h - 1 - ty) * w * 4;
            float v = (ty + 0.5f) / h;                     // 0 top .. 1 bottom
            for (int tx = 0; tx < w; tx++)
            {
                float x = tx + 0.5f;
                // ragged top and bottom edge
                float top = 0.10f + 0.06f * Noise(x / 9f, 0.5f, seed + 1);
                float bot = 0.90f - 0.06f * Noise(x / 9f, 7.5f, seed + 2);
                float a = SmoothStep(top - 0.04f, top + 0.04f, v) * (1f - SmoothStep(bot - 0.04f, bot + 0.04f, v));
                // chisel tip: ends cut on a slant
                float slant = (v - 0.5f) * h * 0.45f;
                float left = 0.06f * h + slant, right = w - 0.06f * h + slant;
                a *= SmoothStep(left - 1.5f, left + 1.5f, x) * (1f - SmoothStep(right - 1.5f, right + 1.5f, x));
                // streaks along the stroke, darker overlap at the ends
                float streak = 0.82f + 0.18f * Noise(x / 60f, v * 9f, seed + 3);
                float ends = 1f + 0.18f * (MathF.Exp(-(x - left) / (0.4f * h)) + MathF.Exp(-(right - x) / (0.4f * h)));
                a = Clamp01(a * streak * ends) * MaxA;
                int o = row + tx * 4;
                px[o] = ToByte(col.X); px[o + 1] = ToByte(col.Y); px[o + 2] = ToByte(col.Z); px[o + 3] = ToByte(a);
            }
        }
        return px;
    }

    // ---------------------------------------------------------------- helpers

    private static float Cover(float pixel, float centre, float thickness)
    {
        float lo = MathF.Max(pixel, centre - thickness * 0.5f), hi = MathF.Min(pixel + 1f, centre + thickness * 0.5f);
        return Clamp01(hi - lo);
    }

    private static void Splat(float[] buf, int w, int h, float x, float y, float v)
    {
        int x0 = (int)MathF.Floor(x - 0.5f), y0 = (int)MathF.Floor(y - 0.5f);
        float fx = x - 0.5f - x0, fy = y - 0.5f - y0;
        Add(buf, w, h, x0, y0, v * (1 - fx) * (1 - fy));
        Add(buf, w, h, x0 + 1, y0, v * fx * (1 - fy));
        Add(buf, w, h, x0, y0 + 1, v * (1 - fx) * fy);
        Add(buf, w, h, x0 + 1, y0 + 1, v * fx * fy);
    }

    private static void Add(float[] buf, int w, int h, int x, int y, float v)
    {
        if ((uint)x < (uint)w && (uint)y < (uint)h) buf[y * w + x] += v;
    }

    private static float RoundRect(float x, float y, float hw, float hh, float r)
    {
        float qx = MathF.Abs(x) - (hw - r), qy = MathF.Abs(y) - (hh - r);
        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    /// <summary>Integer hash to [0, 1].</summary>
    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    /// <summary>Value noise: hashed lattice values, smoothstep-interpolated. [0, 1].</summary>
    private static float Noise(float x, float y, int seed)
    {
        float flx = MathF.Floor(x), fly = MathF.Floor(y);
        int ix = (int)flx, iy = (int)fly;
        float fx = x - flx, fy = y - fly;
        float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
        float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
        return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
    }

    /// <summary>Fractal sum of value noise, lacunarity 2.03, gain 0.5, normalised to [0, 1].</summary>
    private static float Fbm(float x, float y, int octaves, int seed)
    {
        float sum = 0f, amp = 0.5f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Noise(x, y, seed + o * 1013);
            norm += amp;
            x *= 2.03f; y *= 2.03f; amp *= 0.5f;
        }
        return sum / norm;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static float SmoothStep(float a, float b, float v) { float t = Clamp01((v - a) / (b - a)); return t * t * (3f - 2f * t); }
    private static byte ToByte(float v) => (byte)(Clamp01(v) * 255f + 0.5f);

    private readonly struct Vec3
    {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 operator *(Vec3 a, float k) => new(a.X * k, a.Y * k, a.Z * k);
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    }
}
