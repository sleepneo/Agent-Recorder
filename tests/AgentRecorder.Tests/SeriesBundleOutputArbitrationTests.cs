using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static JsonObject SeriesBundleRequest(Fixture f, string directory, string name = "frames")
    {
        var request = f.Request("display", bundle: false);
        request["mode"] = "screenshot_series";
        request.Remove("stop_condition"); request.Remove("video"); request.Remove("artifacts");
        request["interval_ms"] = 1000; request["max_count"] = 1;
        request["output"] = new JsonObject { ["directory"] = directory, ["filename_template"] = name, ["conflict_policy"] = "rename" };
        return request;
    }
    private static void ConfigureSeriesBundle(Fixture f, IScreenshotFrameRunner runner)
    {
        f.Engine.CapturePlanFactoryForTests = cfg => cfg.IsScreenshotSeries
            ? CaptureBackendSelector.BuildScreenshotSeriesPlan(cfg)
            : CaptureBackendSelector.BuildPlan(cfg, new Probe());
        f.Engine.ScreenshotFrameRunnerFactoryForTests = _ => runner;
    }
    private static void SaveSeriesBundleEvidence(string name, object value)
    {
        var directory = Environment.GetEnvironmentVariable("TASK379_EVIDENCE_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }



    private sealed class SeriesBundlePngRunner : IScreenshotFrameRunner
    {
        internal int Calls;
        internal Func<ScreenshotFrameRequest, CancellationToken, Task>? Before;
        public async Task<ScreenshotFrameResult> CaptureAsync(ScreenshotFrameRequest request, CaptureAuthorizationProof proof, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (Before is not null) await Before(request, token);
            var bytes = Png(64, 64); File.WriteAllBytes(request.TempPath, bytes);
            return new(true, "", 64, 64, bytes.Length, DateTime.UtcNow, DateTime.UtcNow);
        }
        public static byte[] Png(int width, int height)
        {
            var raw = new byte[checked((width * 4 + 1) * height)];
            for (int row = 0; row < height; row++)
                raw[row * (width * 4 + 1)] = 0;

            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
                    zlib.Write(raw);
                compressed = output.ToArray();
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteInt(ihdr, 0, width);
            WriteInt(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", compressed);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            WriteInt(stream, data.Length);
            stream.Write(typeBytes);
            stream.Write(data);
            var crcInput = new byte[typeBytes.Length + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
            Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
            WriteInt(stream, unchecked((int)Crc32(crcInput)));
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteInt(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static uint Crc32(byte[] bytes)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            return crc ^ 0xffffffffu;
        }
    }
}
