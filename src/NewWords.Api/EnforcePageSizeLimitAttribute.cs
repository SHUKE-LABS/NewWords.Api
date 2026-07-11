using Microsoft.AspNetCore.Mvc.Filters;

namespace NewWords.Api
{
    [AttributeUsage(AttributeTargets.Method)]
    public class EnforcePageSizeLimitAttribute(int maxPageSize) : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ActionArguments.TryGetValue("pageSize", out var pageSizeObj) && pageSizeObj is int pageSize)
            {
                // Clamp into [1, maxPageSize]: over-cap requests are silently capped;
                // non-positive values fall back to 1 to avoid an unbounded query.
                if (pageSize > maxPageSize)
                {
                    context.ActionArguments["pageSize"] = maxPageSize;
                }
                else if (pageSize <= 0)
                {
                    context.ActionArguments["pageSize"] = 1;
                }
            }

            if (context.ActionArguments.TryGetValue("pageNumber", out var pageNumberObj) && pageNumberObj is int pageNumber
                && pageNumber <= 0)
            {
                context.ActionArguments["pageNumber"] = 1;
            }

            base.OnActionExecuting(context);
        }
    }
}
