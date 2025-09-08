using Microsoft.AspNetCore.Mvc.Filters;
using Serilog.Context;

namespace ModPackUpdater.Filters;

public sealed class ComponentLoggingFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = context.Controller.GetType().Name;
        var action = context.ActionDescriptor.DisplayName ?? "Action";
        using (LogContext.PushProperty("Component", $"API:{controller}"))
        using (LogContext.PushProperty("Action", action))
        {
            await next();
        }
    }
}
