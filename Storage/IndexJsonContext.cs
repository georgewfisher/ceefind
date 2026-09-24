using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using CeeFind.BetterQueue;

namespace CeeFind.Storage
{
    /// <summary>
    /// Tells the serialiser at build time exactly which shapes are written to and read
    /// from the index.
    ///
    /// Without this, serialisation is resolved by reflection at runtime, which the trimmer
    /// cannot see through: it removes the property accessors as unused and the tool then
    /// fails with "Property Get method was not found" on the first search that touches a
    /// stored value. That failure is silent in the sense that it only appears once an
    /// index exists, so a trimmed build looks healthy until the second search.
    ///
    /// Every type named here is a column in the index. Adding a new stored shape means
    /// adding it here too, or it will work untrimmed and fail in the published build.
    /// </summary>
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
    [JsonSerializable(typeof(Histogram))]
    [JsonSerializable(typeof(Dictionary<string, Edge>))]
    [JsonSerializable(typeof(Dictionary<string, long>))]
    [JsonSerializable(typeof(Dictionary<string, DateTime>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Metrics))]
    [JsonSerializable(typeof(SearchSettings))]
    internal partial class IndexJsonContext : JsonSerializerContext
    {
    }
}
