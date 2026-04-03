using DbProxy.Auth;

namespace DbProxy.Tests.Unit;

public class SessionManagerTests : IDisposable
{
    private readonly SessionManager _manager = new(TimeSpan.FromMinutes(5));

    public void Dispose() => _manager.Dispose();

    private AgentSession CreateTestSession(string id = "sess_test", int? budget = null)
    {
        return _manager.CreateSession(id, "agent", "task", budget, DateTime.UtcNow.AddHours(1));
    }

    [Fact]
    public void IncrementQueryCount_NoBudget_AlwaysReturnsTrue()
    {
        CreateTestSession(budget: null);

        for (int i = 0; i < 100; i++)
            Assert.True(_manager.IncrementQueryCount("sess_test"));
    }

    [Fact]
    public void IncrementQueryCount_WithBudget_AllowsUpToBudget()
    {
        CreateTestSession(budget: 5);

        for (int i = 0; i < 5; i++)
            Assert.True(_manager.IncrementQueryCount("sess_test"));
    }

    [Fact]
    public void IncrementQueryCount_WithBudget_RejectsBeyondBudget()
    {
        CreateTestSession(budget: 3);

        for (int i = 0; i < 3; i++)
            _manager.IncrementQueryCount("sess_test");

        Assert.False(_manager.IncrementQueryCount("sess_test"));
    }

    [Fact]
    public void IncrementQueryCount_WithBudget_DoesNotOvercount()
    {
        var session = CreateTestSession(budget: 5);

        for (int i = 0; i < 5; i++)
            _manager.IncrementQueryCount("sess_test");

        // Rejected queries should not increment the counter
        Assert.False(_manager.IncrementQueryCount("sess_test"));
        Assert.False(_manager.IncrementQueryCount("sess_test"));
        Assert.False(_manager.IncrementQueryCount("sess_test"));

        Assert.Equal(5, session.QueriesUsed);
    }

    [Fact]
    public void IncrementQueryCount_CountMatchesBudgetAtExhaustion()
    {
        var session = CreateTestSession(budget: 10);

        int succeeded = 0;
        for (int i = 0; i < 15; i++)
        {
            if (_manager.IncrementQueryCount("sess_test"))
                succeeded++;
        }

        Assert.Equal(10, succeeded);
        Assert.Equal(10, session.QueriesUsed);
    }

    [Fact]
    public void IncrementQueryCount_UnknownSession_ReturnsFalse()
    {
        Assert.False(_manager.IncrementQueryCount("sess_nonexistent"));
    }

    [Fact]
    public void IncrementQueryCount_ConcurrentAccess_DoesNotExceedBudget()
    {
        var session = CreateTestSession("sess_concurrent", budget: 100);

        var succeeded = 0;
        var tasks = new Task[20];
        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    if (_manager.IncrementQueryCount("sess_concurrent"))
                        Interlocked.Increment(ref succeeded);
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.Equal(100, succeeded);
        Assert.Equal(100, session.QueriesUsed);
    }

    [Fact]
    public void GetRemainingBudget_DecreasesCorrectly()
    {
        CreateTestSession(budget: 5);

        Assert.Equal(5, _manager.GetRemainingBudget("sess_test"));

        _manager.IncrementQueryCount("sess_test");
        Assert.Equal(4, _manager.GetRemainingBudget("sess_test"));

        for (int i = 0; i < 4; i++)
            _manager.IncrementQueryCount("sess_test");

        Assert.Equal(0, _manager.GetRemainingBudget("sess_test"));

        // Rejected queries shouldn't make it go negative
        _manager.IncrementQueryCount("sess_test");
        Assert.Equal(0, _manager.GetRemainingBudget("sess_test"));
    }
}
