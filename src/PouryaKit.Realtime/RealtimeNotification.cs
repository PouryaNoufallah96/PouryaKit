using System.Text.Json;

namespace PouryaKit.Realtime;

// The payload of the PostgreSQL notification one push writes, on the one PostgreSQL channel every instance's listener
// listens on, in the Host's HTTP JSON, which AddPouryaKitWeb sets to the Kit's: the event reaches a browser as a
// response body would. Body is the pushed event, written by its runtime type and read back as JSON.
internal sealed record RealtimeNotification(string Channel, string Tenant, object Body)
{
    public const string PostgresChannel = "pouryakit_realtime";

    // PostgreSQL's NOTIFY carries a payload "shorter than 8000 bytes" in its default configuration, counted in the
    // database's encoding: UTF-8 bytes in a UTF-8 database.
    public const int Limit = 8000;

    // Null for a payload of another shape, which any session of the database could notify.
    public static RealtimeNotification? Read(string payload, JsonSerializerOptions json)
    {
        try
        {
            return JsonSerializer.Deserialize<RealtimeNotification>(payload, json) is { Channel: not null, Tenant: not null, Body: JsonElement } read
                ? read
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
