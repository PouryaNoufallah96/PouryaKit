// Adapted from ASP.NET API Versioning's ConfigureOpenApiOptions and ApiExplorerTransformer:
// https://github.com/dotnet/aspnet-api-versioning, commit 1a22bb4281c03d44f387169eefa46765a6219118,
// src/AspNetCore/WebApi/src/Asp.Versioning.OpenApi/Configuration/ConfigureOpenApiOptions.cs and
// src/AspNetCore/WebApi/src/Asp.Versioning.OpenApi/Transformers/ApiExplorerTransformer.cs
// It is used under the MIT License, whose notice follows.
//
// MIT License
//
// Copyright (c) .NET Foundation and contributors. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace PouryaKit.Web.OpenApi;

// One OpenAPI document per API version the Modules declared, named by its group name (v1, v2): the framework serves each
// name and includes in it the operations of that group. Only these documents get the Kit's transformers, after the
// document's own configuration; a document a Host adds under another name keeps what its own transformers write. The
// notices name the configured sunset date without reading the clock (ADR 0005).
internal sealed class ApiVersionDocuments(IApiVersionDescriptionProvider versions)
    : IAdditionalOpenApiDocumentNameResolver,
        IPostConfigureOptions<OpenApiOptions>
{
    public IEnumerable<string> ResolveDocumentNames() =>
        versions.ApiVersionDescriptions.Select(version => version.GroupName);

    public void PostConfigure(string? name, OpenApiOptions options)
    {
        if (
            versions.ApiVersionDescriptions.FirstOrDefault(version =>
                string.Equals(version.GroupName, name, StringComparison.OrdinalIgnoreCase)
            )
            is not { } version
        )
        {
            return;
        }

        options.AddDocumentTransformer(
            (document, _, _) =>
            {
                Describe(document, version);
                return Task.CompletedTask;
            }
        );
        options.AddOperationTransformer(
            (operation, context, _) =>
            {
                Describe(operation, context.Description);
                return Task.CompletedTask;
            }
        );
        options.AddOperationTransformer<BearerSecurity>();
        options.AddOperationTransformer<IdempotencyKeyParameter>();
    }

    // The version, the notices of its deprecation and its sunset, and the links of both policies, the deprecation's
    // first: those to an HTML page as a Markdown list in the description, every one in the x-api-versioning extension.
    private static void Describe(OpenApiDocument document, ApiVersionDescription version)
    {
        document.Info.Version = version.ApiVersion.ToString();
        var description = new StringBuilder(document.Info.Description);
        if (version.DeprecationPolicy is not null || version.IsDeprecated)
        {
            AddSentence(description, "This API version is deprecated.");
        }

        if (version.SunsetPolicy?.Date is { } date)
        {
            AddSentence(
                description,
                $"Its sunset date is {date.ToString("R", CultureInfo.InvariantCulture)}."
            );
        }

        LinkHeaderValue[] links =
        [
            .. version.DeprecationPolicy?.Links ?? [],
            .. version.SunsetPolicy?.Links ?? [],
        ];
        var markdown = new StringBuilder();
        foreach (
            var link in links.Where(link =>
                string.Equals(link.Type.Value, "text/html", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            if (markdown.Length > 0)
            {
                markdown.AppendLine();
            }

            var target = link.LinkTarget.OriginalString;
            markdown.Append(
                link.Title.Value is { Length: > 0 } title ? $"- [{title}]({target})"
                : link.LinkTarget.IsAbsoluteUri ? $"- {target}{Environment.NewLine}"
                : $"- <a href=\"{target}\">{target}</a>{Environment.NewLine}"
            );
        }

        if (markdown.Length > 0)
        {
            description
                .AppendLine()
                .AppendLine()
                .AppendLine("### Links")
                .AppendLine()
                .Append(markdown);
        }

        document.Info.Description = description.ToString();
        if (links.Length > 0)
        {
            document.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            document.Extensions["x-api-versioning"] = new JsonNodeExtension(
                new JsonObject { ["links"] = new JsonArray([.. links.Select(ToJson)]) }
            );
        }
    }

    // A deprecated version's operations are deprecated. The API version parameter of the header reader admits only the
    // document's version and says what it is; the URL segment's is substituted into the path.
    private static void Describe(OpenApiOperation operation, ApiDescription api)
    {
        operation.Deprecated |= api.IsDeprecated;
        foreach (var described in api.ParameterDescriptions)
        {
            if (
                described
                    is {
                        ModelMetadata: ApiVersionModelMetadata metadata,
                        DefaultValue: string version
                    }
                && operation.Parameters?.FirstOrDefault(parameter =>
                    parameter.Name == described.Name
                )
                    is OpenApiParameter { Schema: OpenApiSchema schema } parameter
            )
            {
                parameter.Description ??= metadata.Description;
                schema.Enum = [JsonValue.Create(version)];
            }
        }
    }

    private static void AddSentence(StringBuilder text, string sentence)
    {
        if (text.Length > 0)
        {
            if (text[^1] != '.')
            {
                text.Append('.');
            }

            text.Append(' ');
        }

        text.Append(sentence);
    }

    private static JsonObject ToJson(LinkHeaderValue link)
    {
        var json = new JsonObject();
        if (link.Title.HasValue)
        {
            json["title"] = link.Title.Value;
        }

        if (link.Type.HasValue)
        {
            json["type"] = link.Type.Value;
        }

        json["rel"] = link.RelationType.Value;
        json["url"] = link.LinkTarget.ToString();
        return json;
    }
}
