using DbProxy.Configuration;
using DbProxy.Dashboard.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DbProxy.Dashboard;

/// <summary>Tracks whether the setup wizard needs to be shown. Set during startup.</summary>
public class SetupState
{
    public bool SetupRequired { get; set; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAuthAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.Any(m => m is SkipApiKeyAuthAttribute))
            return;

        // Redirect to setup wizard if first-run setup hasn't been completed
        var setupState = context.HttpContext.RequestServices.GetRequiredService<SetupState>();
        if (setupState.SetupRequired)
        {
            context.Result = new RedirectToActionResult("Setup", "Dashboard", null);
            return;
        }

        var config = context.HttpContext.RequestServices.GetRequiredService<ProxyConfig>();
        var cookie = context.HttpContext.Request.Cookies["gatesql_key"];

        if (string.IsNullOrEmpty(cookie) || !config.Auth.ParentApiKeys.Any(k => k.Key == cookie))
        {
            context.Result = new RedirectToActionResult("Login", "Dashboard", null);
        }
    }
}
