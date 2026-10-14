using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportDesk.Application.Approvals;
using SupportDesk.Application.Tickets;
using SupportDesk.Contracts.Common;
using SupportDesk.Domain;
using SupportDesk.Persistence;

namespace SupportDesk.Api.Infrastructure;

/// <summary>
/// Maps exceptions to RFC 7807 problem details with a stable "code" extension. Domain errors expose their message;
/// unexpected errors return a generic message plus the correlation id (details only in logs).
/// </summary>
public sealed class DeskExceptionHandler(ILogger<DeskExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var problem = new ProblemDetails { Instance = http.Request.Path };
        switch (exception)
        {
            case ValidationException v:
                problem.Status = 400;
                problem.Title = "Some fields need attention.";
                problem.Detail = v.Message;
                problem.Extensions["code"] = ErrorCodes.Validation;
                problem.Extensions["errors"] = v.Errors;
                break;
            case ReplyCollisionException rc:
                problem.Status = 409;
                problem.Title = rc.Message;
                problem.Extensions["code"] = rc.Code;
                problem.Extensions["currentSeq"] = rc.Details.CurrentSeq;
                problem.Extensions["newEntries"] = rc.Details.NewEntries;
                break;
            case ConcurrencyConflictException cc:
                problem.Status = 409;
                problem.Title = cc.Message;
                problem.Extensions["code"] = cc.Code;
                try
                {
                    var queries = http.RequestServices.GetRequiredService<TicketQueryService>();
                    var db = http.RequestServices.GetRequiredService<DeskDbContext>();
                    db.ChangeTracker.Clear();
                    problem.Extensions["current"] = await queries.GetDetailAsync(cc.TicketId, ct);
                }
                catch (DomainException) { }
                break;
            case ApprovalRequiredException ar:
                problem.Status = 202;
                problem.Title = ar.Message;
                problem.Extensions["code"] = ar.Code;
                problem.Extensions["approvalId"] = ar.ApprovalId;
                break;
            case LimitExceededException le:
                problem.Status = 402;
                problem.Title = le.Message;
                problem.Extensions["code"] = le.Code;
                problem.Extensions["limit"] = le.Limit;
                break;
            case DomainException d:
                problem.Status = d.StatusCode;
                problem.Title = d.Message;
                problem.Extensions["code"] = d.Code;
                break;
            case CrossTenantWriteException:
                logger.LogCritical(exception, "Cross-tenant write blocked on {Path}", http.Request.Path);
                problem.Status = 403;
                problem.Title = "This operation is not allowed.";
                problem.Extensions["code"] = ErrorCodes.Forbidden;
                break;
            case DbUpdateConcurrencyException:
                problem.Status = 409;
                problem.Title = "The record was changed by someone else. Reload and try again.";
                problem.Extensions["code"] = ErrorCodes.ConcurrencyConflict;
                break;
            case BadHttpRequestException bad:
                problem.Status = bad.StatusCode;
                problem.Title = "The request could not be read.";
                problem.Extensions["code"] = ErrorCodes.Validation;
                break;
            case OperationCanceledException when http.RequestAborted.IsCancellationRequested:
                return true;
            default:
                logger.LogError(exception, "Unhandled error on {Method} {Path}", http.Request.Method, http.Request.Path);
                problem.Status = 500;
                problem.Title = "Something went wrong on our side.";
                problem.Detail = env.IsDevelopment() ? exception.Message : null;
                problem.Extensions["code"] = "internal_error";
                break;
        }
        problem.Extensions["correlationId"] = http.Response.Headers["X-Correlation-Id"].ToString();
        http.Response.StatusCode = problem.Status ?? 500;
        await http.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
        return true;
    }
}
