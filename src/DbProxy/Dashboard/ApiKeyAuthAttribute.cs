using DbProxy.Configuration;
using DbProxy.Dashboard.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DbProxy.Dashboard;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAuthAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(m => m is SkipApiKeyAuthAttribute))
            return;

        var config = context.HttpContext.RequestServices.GetRequiredService<ProxyConfig>();
        var cookie = context.HttpContext.Request.Cookies["gatesql_key"];

        if (string.IsNullOrEmpty(cookie) || !config.Auth.ParentApiKeys.Any(k => k.Key == cookie))
        {
            context.Result = new RedirectToActionResult("Login", "Dashboard", null);
        }
    }
}
