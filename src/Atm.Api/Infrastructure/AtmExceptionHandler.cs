using Atm.Business.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atm.Api.Infrastructure;

/// <summary>Maps every exception to an RFC 7807 ProblemDetails response with a stable <c>errorCode</c>.</summary>
public sealed class AtmExceptionHandler(IProblemDetailsService problemDetails, ILogger<AtmExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail, extensions) = exception switch
        {
            AtmException e => (StatusFor(e), e.Code, TitleFor(e), e.Message, e.Extensions),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "CONCURRENCY_CONFLICT", "Concurrency conflict",
                "The resource was modified by another request. Retry the operation.", null),
            BadHttpRequestException e => (StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, "Bad request", e.Message, null),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "Internal server error",
                "An unexpected error occurred.", (IDictionary<string, object?>?)null)
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception");
        }
        else
        {
            logger.LogInformation("Request failed with {ErrorCode}: {Message}", code, exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://errors.atm-cash.local/{code.ToLowerInvariant().Replace('_', '-')}",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["errorCode"] = code;
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static int StatusFor(AtmException exception) => exception switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException => StatusCodes.Status409Conflict,
        BusinessRuleException => StatusCodes.Status422UnprocessableEntity,
        ForbiddenException => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    };

    private static string TitleFor(AtmException exception) => exception switch
    {
        ValidationException => "Validation failed",
        NotFoundException => "Resource not found",
        ConflictException => "Conflict with current state",
        BusinessRuleException => "Business rule violated",
        ForbiddenException => "Forbidden",
        _ => "Request failed"
    };
}

public static class ProblemDetailsFactoryExtensions
{
    /// <summary>Model-binding errors (e.g. an unknown enum value such as "GBP") use the same ProblemDetails model.</summary>
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Detail = "The request body or parameters are invalid.",
            Type = "https://errors.atm-cash.local/validation-failed",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["errorCode"] = ErrorCodes.ValidationFailed;
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }
}
