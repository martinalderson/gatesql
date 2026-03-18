using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Dashboard.Models;
using DbProxy.Query;
using Microsoft.AspNetCore.Mvc;

namespace DbProxy.Dashboard.Controllers;

public class DashboardController : Controller
{
    private readonly SessionManager _sessionManager;
    private readonly QueryLogger _queryLogger;
    private readonly ProxyConfig _config;

    public DashboardController(SessionManager sessionManager, QueryLogger queryLogger, ProxyConfig config)
    {
        _sessionManager = sessionManager;
        _queryLogger = queryLogger;
        _config = config;
    }

    public IActionResult Index()
    {
        var allSessions = _sessionManager.GetAllSessions();
        var activeSessions = allSessions
            .Where(s => !s.IsRevoked && DateTime.UtcNow < s.ExpiresAt)
            .Select(SessionViewModel.FromSession)
            .ToList();

        var model = new DashboardViewModel
        {
            ActiveSessions = activeSessions,
            RecentQueries = _queryLogger.GetRecentQueries(50).ToList(),
            TotalSessions = allSessions.Count,
            ConnectedNow = allSessions.Count(s => s.IsConnected),
            TotalQueries = allSessions.Sum(s => s.QueriesUsed),
            IdleTimeoutMinutes = _config.Auth.IdleTimeoutMinutes,
        };

        return View(model);
    }

    [HttpPost]
    public IActionResult Revoke(string sessionId)
    {
        _sessionManager.RevokeSession(sessionId);
        return RedirectToAction("Index");
    }
}
