using System.Globalization;
using Microsoft.AspNetCore.Http;
using PouryaKit.Cqrs;

namespace PouryaKit.Web;

/// <summary>
/// An endpoint parameter that reads a <see cref="PageRequest"/> from the query string:
/// <c>?page=2&amp;size=20&amp;sort=name&amp;direction=desc&amp;filter[name][contains]=lamp&amp;filter[category][eq]=…</c>,
/// each part optional. The <c>filter[</c> prefix is read ignoring case, as the other keys are; an operator is written by
/// its exact short name (<c>eq</c>, <c>ne</c>, <c>contains</c>, <c>startswith</c>, <c>gt</c>, <c>gte</c>, <c>lt</c>,
/// <c>lte</c>); a filter value stays text until the query's <see cref="PageDeclaration"/> reads it. What does not parse at all (a page or size that is not a whole
/// number, a direction other than <c>asc</c> or <c>desc</c>, an unknown operator, a malformed filter key, a <c>page</c>,
/// <c>size</c>, <c>sort</c> or <c>direction</c> given twice) is a request that cannot be read, answered 400 as a problem
/// response whose detail names the rule it broke; everything else, such as a size above the maximum, an undeclared sort
/// key or a filter's field and operator given twice, reaches the query's validator and comes back as validation Errors.
/// The endpoint answers with <c>ToHttpResult()</c>, whose 200 body is the Page: <c>items</c>, <c>number</c>,
/// <c>size</c>, <c>total</c>, <c>totalPages</c>, <c>hasNext</c> and <c>hasPrevious</c>.
/// </summary>
public sealed class HttpPageRequest
{
    private const string FilterPrefix = "filter[";

    private HttpPageRequest(PageRequest request) => Request = request;

    /// <summary>The Page the query string asks for, before the query's validator checks it.</summary>
    public PageRequest Request { get; }

    /// <summary>Called by ASP.NET Core to bind the parameter.</summary>
    public static ValueTask<HttpPageRequest?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var query = context.Request.Query;
        var request = new PageRequest();
        if (Single(query, "page") is { } page)
        {
            request = request with { Number = WholeNumber("page", page) };
        }

        if (Single(query, "size") is { } size)
        {
            request = request with { Size = WholeNumber("size", size) };
        }

        if (Single(query, "sort") is { } sort)
        {
            request = request with { Sort = sort };
        }

        if (Single(query, "direction") is { } direction)
        {
            request = request with
            {
                Direction = direction switch
                {
                    "asc" => SortDirection.Ascending,
                    "desc" => SortDirection.Descending,
                    _ => throw new KitBadRequestException("The direction query parameter is asc or desc."),
                },
            };
        }

        List<PageFilter> filters = [];
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith(FilterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // filter[{field}][{operator}]
            var parts = key[FilterPrefix.Length..].Split("][");
            if (parts is not [{ Length: > 0 } field, [.. var name, ']']] || !FilterOperator.TryParseName(name, out var filterOperator))
            {
                throw new KitBadRequestException(
                    "A filter query parameter is filter[field][operator], with an operator such as eq or contains."
                );
            }

            filters.AddRange(values.Select(value => new PageFilter(field, filterOperator, value ?? "")));
        }

        return ValueTask.FromResult<HttpPageRequest?>(new HttpPageRequest(request with { Filters = filters }));
    }

    private static string? Single(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values)
            ? values is [{ } value]
                ? value
                : throw new KitBadRequestException($"The {key} query parameter is given once.")
            : null;

    private static int WholeNumber(string key, string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new KitBadRequestException($"The {key} query parameter is a whole number.");
}
