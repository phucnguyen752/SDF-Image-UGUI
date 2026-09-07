using System;
using System.Threading;
using UnityEngine;

namespace SDFUI.Editor
{
    /// <summary>Exact Euclidean distance to the opposite alpha class, with a half-pixel edge correction.</summary>
    public static class SdfDistanceTransform
    {
        public const int MaxDimension = 8192;
        public const int MaxPixelCount = 16 * 1024 * 1024;

        /// <summary>
        /// Returns signed distances in pixels, positive inside alpha >= threshold, clamped to +/- range.
        /// Uniform masks saturate to the corresponding sign; pixels beyond the image are not implicit seeds.
        /// </summary>
        public static float[] Generate(Color32[] pixels, int width, int height, float alphaThreshold, float range,
            CancellationToken cancellationToken = default)
        {
            if (pixels == null)
                throw new ArgumentNullException(nameof(pixels));
            ValidateDimensions(width, height);
            if (pixels.Length != width * height)
                throw new ArgumentException("Pixel count must match width times height.", nameof(pixels));
            if (!(alphaThreshold > 0f && alphaThreshold < 1f))
                throw new ArgumentOutOfRangeException(nameof(alphaThreshold), "Alpha threshold must be between 0 and 1, exclusive.");
            if (!(range > 0f) || float.IsInfinity(range))
                throw new ArgumentOutOfRangeException(nameof(range), "Distance range must be finite and positive.");

            cancellationToken.ThrowIfCancellationRequested();
            var result = new float[pixels.Length];
            var distances = new float[pixels.Length];
            int lineLength = Math.Max(width, height);
            var input = new float[lineLength];
            var output = new float[lineLength];
            var sites = new int[lineLength];
            var boundaries = new double[lineLength + 1];

            // Each pass finds nearest opposite-class pixel centers in O(width * height).
            for (int pass = 0; pass < 2; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool findInside = pass == 1;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (i % width == 0) cancellationToken.ThrowIfCancellationRequested();
                    bool inside = pixels[i].a / 255f >= alphaThreshold;
                    distances[i] = inside == findInside ? 0f : float.PositiveInfinity;
                }

                Transform2D(distances, width, height, input, output, sites, boundaries, cancellationToken);

                for (int i = 0; i < pixels.Length; i++)
                {
                    if (i % width == 0) cancellationToken.ThrowIfCancellationRequested();
                    bool inside = pixels[i].a / 255f >= alphaThreshold;
                    if (inside == findInside)
                        continue;
                    float distance = Mathf.Min(range, Mathf.Sqrt(distances[i]) - 0.5f);
                    result[i] = inside ? distance : -distance;
                }
            }

            return result;
        }

        internal static void ValidateDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
                (long)width * height > MaxPixelCount)
                throw new ArgumentOutOfRangeException(nameof(width),
                    $"SDF images must have positive dimensions, at most {MaxDimension} per side and {MaxPixelCount} pixels.");
        }

        private static void Transform2D(float[] distances, int width, int height, float[] input,
            float[] output, int[] sites, double[] boundaries, CancellationToken cancellationToken)
        {
            for (int x = 0; x < width; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = 0; y < height; y++)
                    input[y] = distances[y * width + x];
                TransformLine(input, output, height, sites, boundaries);
                for (int y = 0; y < height; y++)
                    distances[y * width + x] = output[y];
            }

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = y * width;
                Array.Copy(distances, offset, input, 0, width);
                TransformLine(input, output, width, sites, boundaries);
                Array.Copy(output, 0, distances, offset, width);
            }
        }

        // Lower envelope of parabolas. Infinite inputs are skipped to avoid infinity-minus-infinity.
        private static void TransformLine(float[] input, float[] output, int length,
            int[] sites, double[] boundaries)
        {
            int last = -1;
            for (int q = 0; q < length; q++)
            {
                if (float.IsPositiveInfinity(input[q]))
                    continue;

                double intersection = double.NegativeInfinity;
                while (last >= 0)
                {
                    int previous = sites[last];
                    intersection = (input[q] + (double)q * q - input[previous] - (double)previous * previous) /
                                   (2d * (q - previous));
                    if (intersection > boundaries[last])
                        break;
                    last--;
                }

                last++;
                sites[last] = q;
                boundaries[last] = last == 0 ? double.NegativeInfinity : intersection;
                boundaries[last + 1] = double.PositiveInfinity;
            }

            if (last < 0)
            {
                for (int q = 0; q < length; q++)
                    output[q] = float.PositiveInfinity;
                return;
            }

            int current = 0;
            for (int q = 0; q < length; q++)
            {
                while (boundaries[current + 1] < q)
                    current++;
                int delta = q - sites[current];
                output[q] = delta * delta + input[sites[current]];
            }
        }
    }
}
