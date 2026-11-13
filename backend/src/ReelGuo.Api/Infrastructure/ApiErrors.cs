using System.Reflection;

namespace ReelGuo.Api.Infrastructure;

/// <summary>Every error body is <c>{ "error": { "code": "...", "message": "..." } }</c>. Clients switch on <c>code</c>; <c>message</c> is a zh-CN fallback.</summary>
/// <remarks><c>trace_id</c> is set on 500 errors so support can find the request in logs and traces.</remarks>
public sealed record ApiError(string Code, string Message, IReadOnlyList<string>? Details = null, string? TraceId = null);

public sealed record ApiErrorResponse(ApiError Error)
{
    public static ApiErrorResponse Of(string code, string message, IReadOnlyList<string>? details = null, string? traceId = null) =>
        new(new ApiError(code, message, details, traceId));
}

public static class ApiResults
{
    public static IResult Error(int statusCode, string code, string message) =>
        TypedResults.Json(ApiErrorResponse.Of(code, message), statusCode: statusCode);
}

internal static class BuildTime
{
    /// <summary>True while `dotnet build` runs the app to write the OpenAPI document; skip anything that needs a database.</summary>
    public static bool IsGeneratingOpenApi =>
        Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
