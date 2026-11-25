using System;

namespace MoveIt.AR
{
    /// <summary>
    /// Fills a masked hole in an image with texture taken from the rest of the image, so that patterns, lines and
    /// shading continue naturally (the idea behind "Content-Aware Fill").
    /// </summary>
    /// <remarks>
    /// Wexler-style completion with PatchMatch (Barnes et al. 2009):
    /// - Work coarse to fine on an image pyramid.
    /// - For every small patch that touches the hole, find a similar patch in the known part of the image
    ///   (the nearest-neighbour field). PatchMatch finds these quickly by passing good matches on to neighbours
    ///   and trying random nearby alternatives.
    /// - Recompute each hole pixel as the weighted average of what the matched patches say it should be.
    /// - Repeat, then refine on the next finer level.
    /// Plain C# with no engine types, so it runs the same in Unity and in a standalone test.
    /// Images are RGB floats in [0, 1], row-major, width * height * 3 values.
    /// </remarks>
    public static class PatchInpainter
    {
        public struct Settings
        {
            /// <summary>Patch radius; patches are (2r+1)².</summary>
            public int patchRadius;
            /// <summary>EM iterations on the coarsest level (finer levels use fewer).</summary>
            public int iterations;
            /// <summary>PatchMatch sweeps per iteration.</summary>
            public int sweeps;
            /// <summary>Coarsest level: the hole is shrunk until its smaller side is about this many pixels.</summary>
            public int coarsestHoleSize;
            /// <summary>
            /// After the last averaging step, give every hole pixel the exact colour its best match points to.
            /// Averaging overlapping patches smooths fine texture (floor grain, wood); this brings it back.
            /// </summary>
            public bool sharpen;
            /// <summary>
            /// Keep the copied texture's detail but take its overall brightness and colour from a smooth blend of
            /// the surroundings, so a patch copied from a sunlit spot doesn't show up as a light blob in a shadow.
            /// </summary>
            public bool matchLighting;
            public int seed;

            public static Settings Default => new Settings
            {
                patchRadius = 3,
                iterations = 8,
                sweeps = 3,
                coarsestHoleSize = 12,
                sharpen = true,
                matchLighting = true,
                seed = 12345,
            };
        }

        /// <summary>
        /// Fills the pixels where <paramref name="hole"/> is true. Returns a new image.
        /// </summary>
        /// <param name="sourceMask">Optional: where texture may be copied from (e.g. only floor pixels when filling
        /// floor), so the fill never borrows from unrelated objects. Null allows the whole known image.</param>
        public static float[] Inpaint(float[] image, bool[] hole, int width, int height, Settings settings,
            bool[] sourceMask = null)
        {
            return Inpaint(image, hole, width, height, settings, sourceMask, out _, out _);
        }

        /// <summary>
        /// Same, also returning for every hole pixel the index of the known pixel its patch was copied from
        /// (-1 elsewhere). Running at low resolution and then copying full-resolution pixels with these matches
        /// (<see cref="CopyMatchesUpscaled"/>) gives a sharp fill for a fraction of the cost.
        /// </summary>
        /// <param name="lighting">Per-pixel brightness correction that was added to the fill (null if
        /// <see cref="Settings.matchLighting"/> is off); pass it to <see cref="CopyMatchesUpscaled"/>.</param>
        public static float[] Inpaint(float[] image, bool[] hole, int width, int height, Settings settings,
            bool[] sourceMask, out int[] matches, out float[] lighting)
        {
            // Build the pyramid: halve until the hole is small, keeping the patch meaningful.
            var levels = 1;
            var holeSize = HoleExtent(hole, width, height);
            while (holeSize / (1 << levels) >= settings.coarsestHoleSize &&
                   Math.Min(width, height) / (1 << levels) >= 4 * (2 * settings.patchRadius + 1))
                levels++;

            var images = new float[levels][];
            var holes = new bool[levels][];
            var sources = new bool[levels][];
            var widths = new int[levels];
            var heights = new int[levels];
            images[0] = (float[])image.Clone();
            holes[0] = (bool[])hole.Clone();
            sources[0] = sourceMask != null ? (bool[])sourceMask.Clone() : null;
            widths[0] = width;
            heights[0] = height;
            for (var l = 1; l < levels; l++)
            {
                Downsample(images[l - 1], holes[l - 1], widths[l - 1], heights[l - 1],
                    out images[l], out holes[l], out widths[l], out heights[l]);
                sources[l] = sources[l - 1] != null
                    ? DownsampleAll(sources[l - 1], widths[l - 1], heights[l - 1], widths[l], heights[l])
                    : null;
            }

            var random = new Random(settings.seed);
            int[] nnf = null;
            int nnfWidth = 0, nnfHeight = 0;

            for (var l = levels - 1; l >= 0; l--)
            {
                var w = widths[l];
                var h = heights[l];
                var img = images[l];
                var hl = holes[l];

                if (l == levels - 1)
                {
                    FillByDiffusion(img, hl, w, h);
                }
                else
                {
                    // Start the hole from the coarser result.
                    var coarse = images[l + 1];
                    var cw = widths[l + 1];
                    var ch = heights[l + 1];
                    for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var i = y * w + x;
                        if (!hl[i])
                            continue;

                        SampleBilinear(coarse, cw, ch, (x + 0.5f) * 0.5f - 0.5f, (y + 0.5f) * 0.5f - 0.5f, img, i * 3);
                    }
                }

                var level = new Level(img, hl, sources[l], w, h, settings.patchRadius);
                level.InitField(random, nnf, nnfWidth, nnfHeight);
                // Rebuild the hole from the upsampled matches rather than keeping the blurry upscaled image:
                // a blurry guess matches flat, featureless areas best and the fill would stay a smudge.
                if (nnf != null)
                    level.Vote();

                var iterations = Math.Max(2, settings.iterations - (levels - 1 - l));
                for (var it = 0; it < iterations; it++)
                {
                    for (var s = 0; s < settings.sweeps; s++)
                        level.PatchMatchSweep(random, (s + it) % 2 == 0);
                    level.Vote();
                }

                if (l == 0 && settings.sharpen)
                {
                    for (var s = 0; s < settings.sweeps; s++)
                        level.PatchMatchSweep(random, s % 2 == 0);
                    level.CopyBestCentres();
                }

                nnf = level.field;
                nnfWidth = w;
                nnfHeight = h;
            }

            matches = new int[width * height];
            for (var i = 0; i < matches.Length; i++)
                matches[i] = hole[i] ? nnf[i] : -1;

            lighting = null;
            if (settings.matchLighting)
            {
                lighting = LightingCorrection(images[0], hole, sourceMask, width, height);
                for (var i = 0; i < hole.Length; i++)
                {
                    if (!hole[i])
                        continue;
                    for (var c = 0; c < 3; c++)
                        images[0][i * 3 + c] = Clamp01(images[0][i * 3 + c] + lighting[i * 3 + c]);
                }
            }

            return images[0];
        }

        /// <summary>
        /// Fills a hole in a large image: inpaints a crop around the hole (three times its size) at no more than
        /// <paramref name="maxSize"/> pixels, then copies full-resolution pixels along the matches found there.
        /// Overwrites the hole pixels of <paramref name="image"/>.
        /// </summary>
        public static void InpaintCropped(float[] image, bool[] hole, int width, int height, Settings settings,
            bool[] sourceMask, int maxSize)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (!hole[y * width + x])
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            if (maxX < 0)
                return;

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            var x0 = Math.Max(0, minX - bw);
            var y0 = Math.Max(0, minY - bh);
            var x1 = Math.Min(width, maxX + 1 + bw);
            var y1 = Math.Min(height, maxY + 1 + bh);
            var cw = x1 - x0;
            var ch = y1 - y0;

            // Full-resolution crop.
            var crop = new float[cw * ch * 3];
            var cropHole = new bool[cw * ch];
            var cropSource = sourceMask != null ? new bool[cw * ch] : null;
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var o = (y0 + y) * width + x0 + x;
                var i = y * cw + x;
                crop[i * 3] = image[o * 3];
                crop[i * 3 + 1] = image[o * 3 + 1];
                crop[i * 3 + 2] = image[o * 3 + 2];
                cropHole[i] = hole[o];
                if (cropSource != null)
                    cropSource[i] = sourceMask[o];
            }

            // Smaller copy to inpaint: each small pixel averages its cell's known pixels, is part of the hole if any
            // of them is, and may be copied from only if all of them may.
            var scale = Math.Min(1f, (float)maxSize / Math.Max(cw, ch));
            var sw = Math.Max(1, (int)(cw * scale));
            var sh = Math.Max(1, (int)(ch * scale));
            var small = new float[sw * sh * 3];
            var smallHole = new bool[sw * sh];
            var smallSource = cropSource != null ? new bool[sw * sh] : null;
            var counts = new int[sw * sh];
            if (smallSource != null)
                for (var i = 0; i < smallSource.Length; i++)
                    smallSource[i] = true;
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var i = y * cw + x;
                var s = Math.Min(sh - 1, (int)((long)y * sh / ch)) * sw + Math.Min(sw - 1, (int)((long)x * sw / cw));
                if (smallSource != null && !cropSource[i])
                    smallSource[s] = false;
                if (cropHole[i])
                {
                    smallHole[s] = true;
                    continue;
                }

                small[s * 3] += crop[i * 3];
                small[s * 3 + 1] += crop[i * 3 + 1];
                small[s * 3 + 2] += crop[i * 3 + 2];
                counts[s]++;
            }

            for (var s = 0; s < counts.Length; s++)
            {
                if (counts[s] == 0)
                    continue;
                small[s * 3] /= counts[s];
                small[s * 3 + 1] /= counts[s];
                small[s * 3 + 2] /= counts[s];
            }

            var filled = Inpaint(small, smallHole, sw, sh, settings, smallSource, out var matches, out var lighting);
            CopyMatchesUpscaled(crop, cropHole, cw, ch, matches, filled, sw, sh, lighting);

            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var i = y * cw + x;
                if (!cropHole[i])
                    continue;
                var o = (y0 + y) * width + x0 + x;
                image[o * 3] = crop[i * 3];
                image[o * 3 + 1] = crop[i * 3 + 1];
                image[o * 3 + 2] = crop[i * 3 + 2];
            }
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        // Difference between a smooth (harmonic) interpolation of the surroundings' local brightness across the
        // hole and the fill's own local brightness. Computed on blocks, then interpolated per pixel.
        // Only pixels texture may be copied from count as the surroundings: a hole whose edge also touches other
        // things (a cabinet behind a stool) must not take on their brightness.
        static float[] LightingCorrection(float[] filled, bool[] hole, bool[] allowed, int w, int h)
        {
            var block = Math.Max(2, Math.Min(w, h) / 64);
            var bw = (w + block - 1) / block;
            var bh = (h + block - 1) / block;
            var fillMean = new float[bw * bh * 3];
            var knownMean = new float[bw * bh * 3];
            var blockHole = new bool[bw * bh];
            var anyKnown = false;
            for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var n = 0;
                var known = 0;
                float f0 = 0, f1 = 0, f2 = 0, k0 = 0, k1 = 0, k2 = 0;
                for (var y = by * block; y < Math.Min(h, (by + 1) * block); y++)
                for (var x = bx * block; x < Math.Min(w, (bx + 1) * block); x++)
                {
                    var i = y * w + x;
                    f0 += filled[i * 3]; f1 += filled[i * 3 + 1]; f2 += filled[i * 3 + 2];
                    n++;
                    if (hole[i] || (allowed != null && !allowed[i]))
                        continue;
                    k0 += filled[i * 3]; k1 += filled[i * 3 + 1]; k2 += filled[i * 3 + 2];
                    known++;
                }

                var b = by * bw + bx;
                fillMean[b * 3] = f0 / n; fillMean[b * 3 + 1] = f1 / n; fillMean[b * 3 + 2] = f2 / n;
                // Blocks the hole touches (or with nothing to copy from) get their brightness from the harmonic
                // fill below.
                blockHole[b] = known < n;
                if (!blockHole[b])
                    anyKnown = true;
                if (known > 0)
                {
                    knownMean[b * 3] = k0 / known; knownMean[b * 3 + 1] = k1 / known; knownMean[b * 3 + 2] = k2 / known;
                }
            }

            var correction = new float[w * h * 3];
            if (!anyKnown)
                return correction;

            // Smooth membrane across the hole blocks: start from an inward average, then relax (Gauss-Seidel).
            var smooth = (float[])knownMean.Clone();
            FillByDiffusion(smooth, blockHole, bw, bh);
            for (var it = 0; it < 400; it++)
            for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var b = by * bw + bx;
                if (!blockHole[b])
                    continue;
                var n = 0;
                float s0 = 0, s1 = 0, s2 = 0;
                if (bx > 0) { var j = b - 1; s0 += smooth[j * 3]; s1 += smooth[j * 3 + 1]; s2 += smooth[j * 3 + 2]; n++; }
                if (bx < bw - 1) { var j = b + 1; s0 += smooth[j * 3]; s1 += smooth[j * 3 + 1]; s2 += smooth[j * 3 + 2]; n++; }
                if (by > 0) { var j = b - bw; s0 += smooth[j * 3]; s1 += smooth[j * 3 + 1]; s2 += smooth[j * 3 + 2]; n++; }
                if (by < bh - 1) { var j = b + bw; s0 += smooth[j * 3]; s1 += smooth[j * 3 + 1]; s2 += smooth[j * 3 + 2]; n++; }
                smooth[b * 3] = s0 / n; smooth[b * 3 + 1] = s1 / n; smooth[b * 3 + 2] = s2 / n;
            }

            var blockCorrection = new float[bw * bh * 3];
            for (var b = 0; b < bw * bh; b++)
            {
                if (!blockHole[b])
                    continue;
                for (var c = 0; c < 3; c++)
                    blockCorrection[b * 3 + c] = smooth[b * 3 + c] - fillMean[b * 3 + c];
            }

            var tmp = new float[3];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!hole[i])
                    continue;
                SampleBilinear(blockCorrection, bw, bh, (x + 0.5f) / block - 0.5f, (y + 0.5f) / block - 0.5f, tmp, 0);
                correction[i * 3] = tmp[0]; correction[i * 3 + 1] = tmp[1]; correction[i * 3 + 2] = tmp[2];
            }

            return correction;
        }

        /// <summary>
        /// Fills the hole of a full-resolution image using matches found on a smaller copy of it.
        /// </summary>
        /// <param name="full">Full-resolution RGB image; hole pixels are overwritten.</param>
        /// <param name="fullHole">Hole at full resolution.</param>
        /// <param name="smallFill">The inpainted small image, used where no full-resolution source is usable.</param>
        /// <param name="lighting">The correction <see cref="Inpaint"/> returned, or null.</param>
        public static void CopyMatchesUpscaled(float[] full, bool[] fullHole, int fullWidth, int fullHeight,
            int[] matches, float[] smallFill, int smallWidth, int smallHeight, float[] lighting = null)
        {
            var light = new float[3];
            var sx = (float)smallWidth / fullWidth;
            var sy = (float)smallHeight / fullHeight;
            var source = (float[])full.Clone();
            for (var y = 0; y < fullHeight; y++)
            for (var x = 0; x < fullWidth; x++)
            {
                var i = y * fullWidth + x;
                if (!fullHole[i])
                    continue;

                var cx = Math.Min(smallWidth - 1, (int)((long)x * smallWidth / fullWidth));
                var cy = Math.Min(smallHeight - 1, (int)((long)y * smallHeight / fullHeight));
                var m = matches[cy * smallWidth + cx];
                var copied = false;
                if (m >= 0)
                {
                    // Same offset as the small match, scaled up; keeps the sub-pixel position within the cell.
                    var fx = x + (int)Math.Round((m % smallWidth - cx) / sx);
                    var fy = y + (int)Math.Round((m / smallWidth - cy) / sy);
                    if (fx >= 0 && fy >= 0 && fx < fullWidth && fy < fullHeight && !fullHole[fy * fullWidth + fx])
                    {
                        var j = fy * fullWidth + fx;
                        if (lighting != null)
                            SampleBilinear(lighting, smallWidth, smallHeight, (x + 0.5f) * sx - 0.5f,
                                (y + 0.5f) * sy - 0.5f, light, 0);
                        full[i * 3] = Clamp01(source[j * 3] + light[0]);
                        full[i * 3 + 1] = Clamp01(source[j * 3 + 1] + light[1]);
                        full[i * 3 + 2] = Clamp01(source[j * 3 + 2] + light[2]);
                        copied = true;
                    }
                }

                if (!copied)
                {
                    var tmp = new float[3];
                    SampleBilinear(smallFill, smallWidth, smallHeight, (x + 0.5f) * sx - 0.5f, (y + 0.5f) * sy - 0.5f,
                        tmp, 0);
                    full[i * 3] = tmp[0];
                    full[i * 3 + 1] = tmp[1];
                    full[i * 3 + 2] = tmp[2];
                }
            }
        }

        // A coarse pixel is allowed only if all four fine pixels are.
        static bool[] DownsampleAll(bool[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new bool[dw * dh];
            for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
            {
                var all = true;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                    all &= src[Math.Min(sh - 1, 2 * y + dy) * sw + Math.Min(sw - 1, 2 * x + dx)];
                dst[y * dw + x] = all;
            }

            return dst;
        }

        // Size of the hole's bounding box (its larger side), to pick the number of pyramid levels.
        static int HoleExtent(bool[] hole, int w, int h)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!hole[y * w + x])
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            return maxX < 0 ? 0 : Math.Max(maxX - minX + 1, maxY - minY + 1);
        }

        static void Downsample(float[] src, bool[] srcHole, int sw, int sh,
            out float[] dst, out bool[] dstHole, out int dw, out int dh)
        {
            dw = Math.Max(1, sw / 2);
            dh = Math.Max(1, sh / 2);
            dst = new float[dw * dh * 3];
            dstHole = new bool[dw * dh];
            for (var y = 0; y < dh; y++)
            for (var x = 0; x < dw; x++)
            {
                var known = 0;
                var r = 0f;
                var g = 0f;
                var b = 0f;
                var anyHole = false;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var sx = Math.Min(sw - 1, 2 * x + dx);
                    var sy = Math.Min(sh - 1, 2 * y + dy);
                    var si = sy * sw + sx;
                    if (srcHole[si])
                    {
                        anyHole = true;
                        continue;
                    }

                    r += src[si * 3];
                    g += src[si * 3 + 1];
                    b += src[si * 3 + 2];
                    known++;
                }

                var di = y * dw + x;
                // A coarse pixel is part of the hole if any of its fine pixels is, so the hole never shrinks away.
                dstHole[di] = anyHole;
                if (known > 0)
                {
                    dst[di * 3] = r / known;
                    dst[di * 3 + 1] = g / known;
                    dst[di * 3 + 2] = b / known;
                }
            }
        }

        static void SampleBilinear(float[] img, int w, int h, float fx, float fy, float[] dst, int dstIndex)
        {
            fx = Math.Max(0f, Math.Min(w - 1f, fx));
            fy = Math.Max(0f, Math.Min(h - 1f, fy));
            var x0 = (int)fx;
            var y0 = (int)fy;
            var x1 = Math.Min(w - 1, x0 + 1);
            var y1 = Math.Min(h - 1, y0 + 1);
            var tx = fx - x0;
            var ty = fy - y0;
            for (var c = 0; c < 3; c++)
            {
                var a = img[(y0 * w + x0) * 3 + c] * (1 - tx) + img[(y0 * w + x1) * 3 + c] * tx;
                var b = img[(y1 * w + x0) * 3 + c] * (1 - tx) + img[(y1 * w + x1) * 3 + c] * tx;
                dst[dstIndex + c] = a * (1 - ty) + b * ty;
            }
        }

        // Rough first guess for the coarsest level: repeatedly average known neighbours inwards.
        static void FillByDiffusion(float[] img, bool[] hole, int w, int h)
        {
            var known = new bool[hole.Length];
            for (var i = 0; i < hole.Length; i++)
                known[i] = !hole[i];

            var changed = true;
            while (changed)
            {
                changed = false;
                var next = (bool[])known.Clone();
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (known[i])
                        continue;

                    var n = 0;
                    float r = 0, g = 0, b = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        var j = ny * w + nx;
                        if (!known[j])
                            continue;
                        r += img[j * 3];
                        g += img[j * 3 + 1];
                        b += img[j * 3 + 2];
                        n++;
                    }

                    if (n == 0)
                        continue;

                    img[i * 3] = r / n;
                    img[i * 3 + 1] = g / n;
                    img[i * 3 + 2] = b / n;
                    next[i] = true;
                    changed = true;
                }

                known = next;
            }
        }

        // One pyramid level: the image being completed, the hole, and the nearest-neighbour field.
        sealed class Level
        {
            readonly float[] m_Image;
            readonly bool[] m_Hole;
            readonly int m_W;
            readonly int m_H;
            readonly int m_R;

            // Target patch centres to match: every centre whose patch touches the hole.
            readonly int[] m_Targets;
            // Valid source centres: patch fully inside the image and fully known.
            readonly bool[] m_ValidSource;
            readonly int[] m_SourceList;

            public int[] field;
            readonly float[] m_Distance;

            public Level(float[] image, bool[] hole, bool[] allowed, int w, int h, int r)
            {
                m_Image = image;
                m_Hole = hole;
                m_W = w;
                m_H = h;
                m_R = r;
                field = new int[w * h];
                m_Distance = new float[w * h];

                // A pixel is "near the hole" when a hole pixel is within the patch radius.
                var nearHole = Dilate(hole, w, h, r);
                var targets = new System.Collections.Generic.List<int>();
                for (var i = 0; i < w * h; i++)
                    if (nearHole[i])
                        targets.Add(i);
                m_Targets = targets.ToArray();

                m_ValidSource = new bool[w * h];
                var sources = new System.Collections.Generic.List<int>();
                for (var y = r; y < h - r; y++)
                for (var x = r; x < w - r; x++)
                {
                    var i = y * w + x;
                    if (!nearHole[i])
                    {
                        m_ValidSource[i] = true;
                        sources.Add(i);
                    }
                }

                m_SourceList = sources.ToArray();

                if (allowed != null)
                {
                    // Source patches must lie fully inside the allowed area too.
                    var outside = new bool[w * h];
                    for (var i = 0; i < w * h; i++)
                        outside[i] = !allowed[i];
                    outside = Dilate(outside, w, h, r);
                    var kept = new System.Collections.Generic.List<int>();
                    foreach (var i in m_SourceList)
                    {
                        if (outside[i])
                            m_ValidSource[i] = false;
                        else
                            kept.Add(i);
                    }

                    // If too little is allowed (tiny floor area), fall back to the whole image rather than fail.
                    if (kept.Count >= 64)
                        m_SourceList = kept.ToArray();
                    else
                        foreach (var i in m_SourceList)
                            m_ValidSource[i] = true;
                }
            }

            static bool[] Dilate(bool[] mask, int w, int h, int r)
            {
                var result = new bool[mask.Length];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if (!mask[y * w + x])
                        continue;
                    for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h)
                            result[ny * w + nx] = true;
                    }
                }

                return result;
            }

            public void InitField(Random random, int[] coarse, int cw, int ch)
            {
                if (m_SourceList.Length == 0)
                    throw new InvalidOperationException("No known area to copy from.");

                foreach (var t in m_Targets)
                {
                    var s = -1;
                    if (coarse != null)
                    {
                        // Upsample the coarser field: same offset, doubled.
                        var tx = t % m_W;
                        var ty = t / m_W;
                        var cx = Math.Min(cw - 1, tx / 2);
                        var cy = Math.Min(ch - 1, ty / 2);
                        var cs = coarse[cy * cw + cx];
                        var sx = (cs % cw) * 2 + (tx % 2);
                        var sy = (cs / cw) * 2 + (ty % 2);
                        if (sx >= 0 && sy >= 0 && sx < m_W && sy < m_H && m_ValidSource[sy * m_W + sx])
                            s = sy * m_W + sx;
                    }

                    if (s < 0)
                        s = m_SourceList[random.Next(m_SourceList.Length)];

                    field[t] = s;
                    m_Distance[t] = PatchDistance(t, s, float.MaxValue);
                }
            }

            // Mean squared colour difference between the patches at a and b.
            float PatchDistance(int a, int b, float limit)
            {
                var ax = a % m_W;
                var ay = a / m_W;
                var bx = b % m_W;
                var by = b / m_W;
                var sum = 0f;
                var count = 0;
                for (var dy = -m_R; dy <= m_R; dy++)
                {
                    var py = ay + dy;
                    if (py < 0 || py >= m_H)
                        continue;
                    var qy = by + dy;
                    for (var dx = -m_R; dx <= m_R; dx++)
                    {
                        var px = ax + dx;
                        if (px < 0 || px >= m_W)
                            continue;
                        var p = (py * m_W + px) * 3;
                        var q = (qy * m_W + bx + dx) * 3;
                        var d0 = m_Image[p] - m_Image[q];
                        var d1 = m_Image[p + 1] - m_Image[q + 1];
                        var d2 = m_Image[p + 2] - m_Image[q + 2];
                        sum += d0 * d0 + d1 * d1 + d2 * d2;
                        count++;
                    }

                }

                // Normalise, so patches clipped by the image edge compare fairly.
                return count == 0 ? float.MaxValue : sum / count;
            }

            void TryCandidate(int t, int s)
            {
                if (s < 0 || s >= m_ValidSource.Length || !m_ValidSource[s])
                    return;

                var d = PatchDistance(t, s, m_Distance[t]);
                if (d < m_Distance[t])
                {
                    m_Distance[t] = d;
                    field[t] = s;
                }
            }

            // Propagation (a neighbour's match, shifted by one, is probably good here too) and random search
            // (try random sources in shrinking windows around the current match).
            public void PatchMatchSweep(Random random, bool forward)
            {
                var step = forward ? -1 : 1;
                var n = m_Targets.Length;
                for (var k = 0; k < n; k++)
                {
                    var t = m_Targets[forward ? k : n - 1 - k];
                    var tx = t % m_W;
                    var ty = t / m_W;

                    // Propagation from the left/up (or right/down) neighbour. (Shifts that leave the row land on
                    // an edge column, which is never a valid source, so they are rejected.)
                    var nx = tx + step;
                    if (nx >= 0 && nx < m_W)
                    {
                        var ns = field[ty * m_W + nx];
                        TryCandidate(t, ns - step);
                    }

                    var ny = ty + step;
                    if (ny >= 0 && ny < m_H)
                    {
                        var ns = field[ny * m_W + tx];
                        TryCandidate(t, ns - step * m_W);
                    }

                    // Random search.
                    var cur = field[t];
                    var cx = cur % m_W;
                    var cy = cur / m_W;
                    for (var radius = Math.Max(m_W, m_H); radius >= 1; radius /= 2)
                    {
                        var rx = cx + random.Next(-radius, radius + 1);
                        var ry = cy + random.Next(-radius, radius + 1);
                        if (rx < 0 || ry < 0 || rx >= m_W || ry >= m_H)
                            continue;
                        TryCandidate(t, ry * m_W + rx);
                    }
                }
            }

            // Each hole pixel takes the centre pixel of the source patch matched to the patch centred on it.
            public void CopyBestCentres()
            {
                var result = (float[])m_Image.Clone();
                foreach (var t in m_Targets)
                {
                    if (!m_Hole[t])
                        continue;
                    var s = field[t] * 3;
                    result[t * 3] = m_Image[s];
                    result[t * 3 + 1] = m_Image[s + 1];
                    result[t * 3 + 2] = m_Image[s + 2];
                }

                Array.Copy(result, m_Image, m_Image.Length);
            }

            // Each hole pixel becomes the weighted average of what every matched patch covering it says,
            // weighting good matches more.
            public void Vote()
            {
                var sum = new float[m_W * m_H * 3];
                var weight = new float[m_W * m_H];

                // Typical match distance, to scale the weights.
                var mean = 0f;
                foreach (var t in m_Targets)
                    mean += m_Distance[t];
                mean = Math.Max(1e-4f, mean / Math.Max(1, m_Targets.Length));

                foreach (var t in m_Targets)
                {
                    var s = field[t];
                    var tx = t % m_W;
                    var ty = t / m_W;
                    var sx = s % m_W;
                    var sy = s / m_W;
                    var wgt = (float)Math.Exp(-m_Distance[t] / (2f * mean));
                    for (var dy = -m_R; dy <= m_R; dy++)
                    {
                        var py = ty + dy;
                        if (py < 0 || py >= m_H)
                            continue;
                        for (var dx = -m_R; dx <= m_R; dx++)
                        {
                            var px = tx + dx;
                            if (px < 0 || px >= m_W)
                                continue;
                            var p = py * m_W + px;
                            if (!m_Hole[p])
                                continue;
                            var q = ((sy + dy) * m_W + sx + dx) * 3;
                            sum[p * 3] += m_Image[q] * wgt;
                            sum[p * 3 + 1] += m_Image[q + 1] * wgt;
                            sum[p * 3 + 2] += m_Image[q + 2] * wgt;
                            weight[p] += wgt;
                        }
                    }
                }

                for (var p = 0; p < m_W * m_H; p++)
                {
                    if (!m_Hole[p] || weight[p] <= 0f)
                        continue;
                    m_Image[p * 3] = sum[p * 3] / weight[p];
                    m_Image[p * 3 + 1] = sum[p * 3 + 1] / weight[p];
                    m_Image[p * 3 + 2] = sum[p * 3 + 2] / weight[p];
                }

                // Distances changed with the image; refresh them for the next sweep.
                foreach (var t in m_Targets)
                    m_Distance[t] = PatchDistance(t, field[t], float.MaxValue);
            }
        }
    }
}
