namespace MicBridge.App.Serialization;

internal sealed class JsonSerializerOptions { public bool WriteIndented { get; set; } }
internal sealed class JsonException(string message, Exception inner) : Exception(message, inner);
internal static class JsonSerializer
{
    internal static string Serialize<T>(T value, JsonSerializerOptions? options = null)
    {
#if NETFRAMEWORK
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, options?.WriteIndented == true ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None);
#else
        return System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = options?.WriteIndented == true });
#endif
    }
    internal static T? Deserialize<T>(string text)
    {
        try
        {
#if NETFRAMEWORK
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(text);
#else
            return System.Text.Json.JsonSerializer.Deserialize<T>(text);
#endif
        }
#if NETFRAMEWORK
        catch (Newtonsoft.Json.JsonException error) { throw new JsonException(error.Message, error); }
#else
        catch (System.Text.Json.JsonException error) { throw new JsonException(error.Message, error); }
#endif
    }
}
