using System;
using NUnit.Framework;
using SDFUI.Editor;
using UnityEngine;

namespace SDFUI.Tests
{
    public sealed class SdfDistanceTransformTests
    {
        [Test]
        public void RandomBinaryMasks_MatchIndependentNearestBoundarySearch()
        {
            var random = new System.Random(57193);
            for (int iteration = 0; iteration < 40; iteration++)
            {
                int width = random.Next(1, 11);
                int height = random.Next(1, 11);
                float range = iteration % 2 == 0 ? 1.25f : 12;
                var pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(255, 255, 255, random.Next(2) == 0 ? (byte)0 : (byte)255);

                float[] actual = SdfDistanceTransform.Generate(pixels, width, height, 0.5f, range);
                Assert.That(actual.Length, Is.EqualTo(pixels.Length));
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float expected = NearestBoundary(pixels, width, height, x, y, range);
                    Assert.That(actual[y * width + x], Is.EqualTo(expected).Within(0.0001f),
                        $"Mask {iteration} ({width}x{height}), pixel ({x}, {y})");
                }
            }
        }

        [TestCase(0, -7f)]
        [TestCase(255, 7f)]
        public void MaskWithoutOppositePixels_SaturatesAtRange(int alpha, float expected)
        {
            var pixels = new Color32[35];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(255, 255, 255, (byte)alpha);
            float[] actual = SdfDistanceTransform.Generate(pixels, 7, 5, 0.5f, 7);
            foreach (float distance in actual)
                Assert.That(distance, Is.EqualTo(expected));
        }

        [Test]
        public void AlphaThreshold_DefinesBoundaryBetweenAdjacentPixels()
        {
            var pixels = new[] { new Color32(0, 0, 0, 127), new Color32(0, 0, 0, 128) };
            Assert.That(SdfDistanceTransform.Generate(pixels, 2, 1, 0.5f, 4),
                Is.EqualTo(new[] { -0.5f, 0.5f }).Within(0.0001f));
        }

        [Test]
        public void InvalidInput_IsRejectedBeforeAllocatingDistanceFields()
        {
            var pixels = new Color32[4];
            Assert.Throws<ArgumentNullException>(() => SdfDistanceTransform.Generate(null, 2, 2, 0.5f, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => SdfDistanceTransform.Generate(pixels, 0, 2, 0.5f, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => SdfDistanceTransform.Generate(pixels, 2, -1, 0.5f, 4));
            Assert.Throws<ArgumentException>(() => SdfDistanceTransform.Generate(pixels, 3, 2, 0.5f, 4));
            foreach (float threshold in new[] { 0f, 1f, float.NaN, float.PositiveInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => SdfDistanceTransform.Generate(pixels, 2, 2, threshold, 4));
            foreach (float range in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                Assert.Throws<ArgumentOutOfRangeException>(() => SdfDistanceTransform.Generate(pixels, 2, 2, 0.5f, range));
        }

        private static float NearestBoundary(Color32[] pixels, int width, int height, int x, int y, float range)
        {
            bool inside = pixels[y * width + x].a >= 128;
            double nearestSquared = double.PositiveInfinity;
            for (int otherY = 0; otherY < height; otherY++)
            for (int otherX = 0; otherX < width; otherX++)
            {
                if ((pixels[otherY * width + otherX].a >= 128) == inside)
                    continue;
                int dx = x - otherX;
                int dy = y - otherY;
                nearestSquared = Math.Min(nearestSquared, dx * dx + dy * dy);
            }
            float magnitude = (float)Math.Min(range, Math.Sqrt(nearestSquared) - 0.5);
            return inside ? magnitude : -magnitude;
        }
    }
}
