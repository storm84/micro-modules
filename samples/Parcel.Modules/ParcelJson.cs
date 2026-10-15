using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Parcel.Modules;

internal static class ParcelJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void AddJson(NpgsqlCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value, Json));

    internal static T Deserialize<T>(string payload)
        => JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidOperationException("Stored payload is null.");
}
