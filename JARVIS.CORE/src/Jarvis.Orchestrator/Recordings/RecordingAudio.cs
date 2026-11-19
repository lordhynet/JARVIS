using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Recordings;

public static class RecordingAudio
{
    public const int MaximumChunkBytes = 1_920_044;
    // Canonical 16 kHz mono PCM WAV. Chunk duration comes from validated samples, never a client claim.
    public static int Samples(ReadOnlySpan<byte> bytes)
    {
        Guard.Require(bytes.Length is > 44 and <= MaximumChunkBytes && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WAVE"u8) &&
            bytes[12..16].SequenceEqual("fmt "u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes[16..20]) == 16 &&
            BinaryPrimitives.ReadInt16LittleEndian(bytes[20..22]) == 1 && BinaryPrimitives.ReadInt16LittleEndian(bytes[22..24]) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes[24..28]) == 16000 && BinaryPrimitives.ReadInt32LittleEndian(bytes[28..32]) == 32000 &&
            BinaryPrimitives.ReadInt16LittleEndian(bytes[32..34]) == 2 && BinaryPrimitives.ReadInt16LittleEndian(bytes[34..36]) == 16 &&
            bytes[36..40].SequenceEqual("data"u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes[40..44]) == bytes.Length - 44 &&
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]) == bytes.Length - 8 && (bytes.Length - 44) % 2 == 0,
            400, "Recording chunks must be bounded canonical 16 kHz mono PCM WAV.");
        return (bytes.Length - 44) / 2;
    }
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static byte[] AssociatedData(RecordingChunk chunk) => Encoding.UTF8.GetBytes($"jarvis-recording-v1:{chunk.Epoch}:{chunk.RecordingId:N}:{chunk.Track}:{chunk.Sequence}:{chunk.Sha256}:{chunk.Samples}:{chunk.BlobId:N}");
}
