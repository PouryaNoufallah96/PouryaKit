using Microsoft.AspNetCore.Http;

namespace PouryaKit.Web;

// A request the Kit itself refuses as unreadable, such as one without its Idempotency-Key, a page query string that does
// not parse or a trusted peer's incomplete context. UnexpectedExceptionHandler answers it 400 with this message, the rule
// the request broke, as the problem's detail; ASP.NET Core's own BadHttpRequestException keeps no detail, because its
// message quotes what the caller sent.
internal sealed class KitBadRequestException(string message) : BadHttpRequestException(message);
