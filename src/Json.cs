using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus;

/// <summary>Source-generated (reflection-free, AOT-clean) serialization for the small JSON files.
/// The high-volume JSONL streams (changes, stats, snapshot, dirmap) are written with a raw
/// Utf8JsonWriter instead and only READ through this context.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ArgusConfig))]
[JsonSerializable(typeof(CursorState))]
[JsonSerializable(typeof(SnapshotLine))]
[JsonSerializable(typeof(DirMapLine))]
public sealed partial class JsonCtx : JsonSerializerContext;

public static class Jsonl
{
    /// <summary>Writer options for all JSONL output: compact, and Greek filenames stay literal
    /// (UnsafeRelaxedJsonEscaping — local files, not web output).</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
