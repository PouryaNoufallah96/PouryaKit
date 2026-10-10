using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PouryaKit.Domain;

public static class JsonSerializerOptionsExtensions
{
    extension(JsonSerializerOptions options)
    {
        /// <summary>
        /// Sets the Kit's one JSON representation on these options and returns them: camelCase property names, read
        /// ignoring case, a body that names one of its type's members twice, in any case, refused instead of keeping the
        /// last value, text escaped only where JSON requires it, and every typed ID written as its bare value
        /// (<see cref="TypedIdJsonConverterFactory"/>). <c>AddPouryaKitWeb</c> applies it to the HTTP options, and
        /// <c>UsePouryaKitWolverine</c> applies <see cref="UsePouryaKitWireJson"/>, the same representation read
        /// strictly, to the options of stored and delivered messages, so a value is written the same way in a response
        /// and in an envelope. Configure an instance ASP.NET Core or Wolverine hands over, or a new one:
        /// <c>new JsonSerializerOptions().UsePouryaKitJson()</c>. Applying it twice changes nothing.
        /// </summary>
        public JsonSerializerOptions UsePouryaKitJson()
        {
            ArgumentNullException.ThrowIfNull(options);
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PropertyNameCaseInsensitive = true;
            options.AllowDuplicateProperties = false;
            options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
            if (!options.Converters.Any(converter => converter is TypedIdJsonConverterFactory))
            {
                options.Converters.Add(new TypedIdJsonConverterFactory());
            }

            return options;
        }

        /// <summary>
        /// Sets the Kit's JSON representation (<see cref="UsePouryaKitJson"/>) on these options, read strictly, and
        /// returns them: a body that lacks a member its type requires, a constructor parameter without a default value
        /// or a <c>required</c> member, or that carries null where the type's nullable annotations forbid it, fails to
        /// read instead of leaving the member's default, and writing such a null fails too. System.Text.Json sees the
        /// annotations of properties and constructor parameters compiled with nullable reference types enabled, not of
        /// collection elements or generic arguments. A member the type does not have is skipped, so a body with a member
        /// added stays readable by a type without it. <c>UsePouryaKitWolverine</c> applies it to the options of stored
        /// and delivered messages; HTTP bodies, cached answers and recorded responses keep
        /// <see cref="UsePouryaKitJson"/>. Applying it twice changes nothing.
        /// </summary>
        public JsonSerializerOptions UsePouryaKitWireJson()
        {
            options.UsePouryaKitJson();
            options.RespectRequiredConstructorParameters = true;
            options.RespectNullableAnnotations = true;
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
            return options;
        }
    }
}
