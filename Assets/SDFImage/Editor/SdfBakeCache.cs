using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;

namespace SDFUI.Editor
{
    public sealed class SdfBakeData
    {
        public string fingerprint;
        public bool sRGB;
        public int width, height, padding;
        public float range, alphaThreshold, ppu;
        public Vector4 border;
        public Vector2 pivot;
        public Color32[] color;
        public ushort[] distanceHalf;
    }

    /// <summary>Disposable local cache. Imported source-image subassets remain the runtime artifacts.</summary>
    public static class SdfBakeCache
    {
        private const int FormatVersion = 2;
        private static readonly Dictionary<string, Hash128> registeredDependencies = new Dictionary<string, Hash128>();
        public static string CacheDirectory => Path.GetFullPath("Library/SDFImage");
        public static string SpriteKey(Sprite sprite) => sprite.GetSpriteID().ToString();

        public static bool TryRead(string path, Sprite sprite, string fingerprint, out SdfBakeData data)
        {
            data = null;
            if (!sprite || string.IsNullOrEmpty(fingerprint)) return false;
            try
            {
                string directory = DirectoryFor(path);
                // A worker's ready marker is reusable internally, but only the main thread may publish it.
                return LatestFingerprint(directory) == fingerprint &&
                    TryReadFile(directory, SpriteKey(sprite), fingerprint, out data);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static bool TryReadLatest(string path, Sprite sprite, out SdfBakeData data)
        {
            data = null;
            if (!sprite) return false;
            try
            {
                string directory = DirectoryFor(path);
                string fingerprint = LatestFingerprint(directory);
                return !string.IsNullOrEmpty(fingerprint) && TryReadFile(directory, SpriteKey(sprite), fingerprint, out data);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static string LatestFingerprint(string directory)
        {
            string latest = Path.Combine(directory, "latest.txt");
            return File.Exists(latest) && new FileInfo(latest).Length <= 4096 ? File.ReadAllText(latest) : null;
        }

        internal static string DependencyName(string path) => "SDFImage/Bake/" + AssetDatabase.AssetPathToGUID(path);

        // Only called by Editor updates/publication, never from an import callback or a worker.
        internal static bool RegisterPublishedDependency(string path) => RegisterDirectoryDependency(DirectoryFor(path));

        private static bool RegisterDirectoryDependency(string directory)
        {
            string name = "SDFImage/Bake/" + Path.GetFileName(directory);
            Hash128 hash = PublishedDependencyHash(directory);
            bool changed = registeredDependencies.TryGetValue(name, out Hash128 previous) ? previous != hash : hash != default;
            // Always restore Unity's registration; this dictionary only coalesces main-thread reimport requests.
            AssetDatabase.RegisterCustomDependency(name, hash);
            registeredDependencies[name] = hash;
            return changed;
        }

        private static Hash128 PublishedDependencyHash(string directory)
        {
            string fingerprint = LatestFingerprint(directory);
            return string.IsNullOrEmpty(fingerprint) ? default : Hash128.Compute(FormatVersion + "|" + fingerprint);
        }

        // Resolve Unity identities on the main thread before handing these paths to worker code.
        internal static string DirectoryFor(string path)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
                throw new ArgumentException("The source image must be a persistent project asset.", nameof(path));
            return Path.Combine(CacheDirectory, guid);
        }

        internal static string FileFor(string directory, string spriteKey, string fingerprint)
            => Path.Combine(directory, spriteKey + "-" + Hash(fingerprint) + ".bin");

        internal static bool BatchReady(string directory, string[] keys, string fingerprint)
        {
            if (!File.Exists(MarkerFor(directory, fingerprint)))
                return false;
            foreach (string key in keys)
                if (!HasValidHeader(FileFor(directory, key, fingerprint), fingerprint))
                    return false;
            return true;
        }

        internal static void CommitBatch(string directory, string fingerprint, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            WriteAtomic(MarkerFor(directory, fingerprint), stream =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(fingerprint);
                stream.Write(bytes, 0, bytes.Length);
            });
        }

        // Publish the tiny latest pointer only after the Editor verifies this generation is still current.
        internal static void PublishLatest(string directory, string fingerprint)
        {
            if (LatestFingerprint(directory) != fingerprint)
            {
                WriteAtomic(Path.Combine(directory, "latest.txt"), stream =>
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(fingerprint);
                    stream.Write(bytes, 0, bytes.Length);
                });
            }
            RegisterDirectoryDependency(directory);
        }

        internal static void Write(string file, SdfBakeData data, CancellationToken token)
        {
            WriteAtomic(file, stream =>
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(FormatVersion);
                    writer.Write(data.fingerprint);
                    writer.Write(data.sRGB);
                    writer.Write(data.width); writer.Write(data.height); writer.Write(data.padding);
                    writer.Write(data.range); writer.Write(data.alphaThreshold); writer.Write(data.ppu);
                    writer.Write(data.border.x); writer.Write(data.border.y); writer.Write(data.border.z); writer.Write(data.border.w);
                    writer.Write(data.pivot.x); writer.Write(data.pivot.y);
                    int width = data.width + data.padding * 2;
                    int height = data.height + data.padding * 2;
                    var row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            Color32 color = data.color[y * width + x];
                            row[x * 4] = color.r; row[x * 4 + 1] = color.g;
                            row[x * 4 + 2] = color.b; row[x * 4 + 3] = color.a;
                        }
                        writer.Write(row);
                    }
                    for (int y = 0; y < height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        for (int x = 0; x < width; x++)
                        {
                            ushort half = data.distanceHalf[y * width + x];
                            row[x * 2] = (byte)half; row[x * 2 + 1] = (byte)(half >> 8);
                        }
                        writer.Write(row, 0, width * 2);
                    }
                    token.ThrowIfCancellationRequested();
                }
            });
        }

        private static bool TryReadFile(string directory, string key, string fingerprint, out SdfBakeData data)
        {
            data = null;
            string file = FileFor(directory, key, fingerprint);
            if (!File.Exists(MarkerFor(directory, fingerprint)) || !File.Exists(file))
                return false;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    if (!ReadHeader(reader, fingerprint, out SdfBakeData value, out int count))
                        return false;
                    value.color = new Color32[count];
                    value.distanceHalf = new ushort[count];
                    int width = value.width + value.padding * 2;
                    int height = value.height + value.padding * 2;
                    var row = new byte[width * 4];
                    for (int y = 0; y < height; y++)
                    {
                        ReadRow(reader, row, width * 4);
                        for (int x = 0; x < width; x++)
                            value.color[y * width + x] = new Color32(row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
                    }
                    for (int y = 0; y < height; y++)
                    {
                        ReadRow(reader, row, width * 2);
                        for (int x = 0; x < width; x++)
                            value.distanceHalf[y * width + x] = (ushort)(row[x * 2] | row[x * 2 + 1] << 8);
                    }
                    data = value;
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static void ReadRow(BinaryReader reader, byte[] row, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = reader.Read(row, offset, length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private static bool HasValidHeader(string file, string fingerprint)
        {
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(file)))
                    return ReadHeader(reader, fingerprint, out _, out _);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static bool ReadHeader(BinaryReader reader, string fingerprint, out SdfBakeData value, out int count)
        {
            value = null;
            count = 0;
            if (reader.ReadInt32() != FormatVersion || reader.BaseStream.Length > 128L * 1024 * 1024) return false;
            value = new SdfBakeData
            {
                fingerprint = reader.ReadString(), sRGB = reader.ReadBoolean(), width = reader.ReadInt32(), height = reader.ReadInt32(),
                padding = reader.ReadInt32(), range = reader.ReadSingle(), alphaThreshold = reader.ReadSingle(), ppu = reader.ReadSingle(),
                border = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                pivot = new Vector2(reader.ReadSingle(), reader.ReadSingle())
            };
            if (value.fingerprint != fingerprint || value.width <= 0 || value.height <= 0 || value.padding < 0 ||
                value.padding > 4096 || !(value.range > 0 && value.range <= 4096) ||
                !(value.alphaThreshold > 0 && value.alphaThreshold < 1) || !(value.ppu > 0) || float.IsInfinity(value.ppu)) return false;
            int width = value.width + value.padding * 2;
            int height = value.height + value.padding * 2;
            SdfDistanceTransform.ValidateDimensions(width, height);
            count = width * height;
            return reader.BaseStream.Length - reader.BaseStream.Position == count * 6L;
        }

        private static string MarkerFor(string directory, string fingerprint) => Path.Combine(directory, Hash(fingerprint) + ".ready");

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteAtomic(string file, Action<FileStream> write)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
