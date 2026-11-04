using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RequestPortal.Application.Messages;

namespace RequestPortal.Web.Security;

/// <summary>Keep expected message access/missing-thread failures as HTTP errors, without masking other exceptions.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MessagePageExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is UnauthorizedAccessException)
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        else if (context.Exception is MessageThreadNotFoundException)
            context.Result = new NotFoundResult();
        else return;
        context.ExceptionHandled = true;
    }
}
