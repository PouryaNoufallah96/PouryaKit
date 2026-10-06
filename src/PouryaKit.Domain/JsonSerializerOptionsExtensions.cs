using System.Text.Encodings.Web;
using System.Text.Json;

namespace PouryaKit.Domain;

public static class JsonSerializerOptionsExtensions
{
    extension(JsonSerializerOptions options)
    {
        /// <summary>
        /// Sets the Kit's one JSON representation on these options and returns them: camelCase property names, read
        /// ignoring case, text escaped only where JSON requires it, and every typed ID written as its bare value
        /// (<see cref="TypedIdJsonConverterFactory"/>). <c>AddPouryaKitWeb</c> applies it to the HTTP options and
        /// <c>UsePouryaKitWolverine</c> to the options of stored and delivered messages, so a value is written the same
        /// way in a response and in an envelope. Configure an instance ASP.NET Core or Wolverine hands over, or a new
        /// one: <c>new JsonSerializerOptions().UsePouryaKitJson()</c>. Applying it twice changes nothing.
        /// </summary>
        public JsonSerializerOptions UsePouryaKitJson()
        {
            ArgumentNullException.ThrowIfNull(options);
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PropertyNameCaseInsensitive = true;
            options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            if (!options.Converters.Any(converter => converter is TypedIdJsonConverterFactory))
            {
                options.Converters.Add(new TypedIdJsonConverterFactory());
            }

            return options;
        }
    }
}
