using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace DbProxy.Tests.Integration;

public class MultiClientTests : IAsyncLifetime
{
    private readonly ProxyFixture _fixture = new();
    private static readonly string ScriptsDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Integration", "ClientScripts"));

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task Psql_CanConnectAndQuery()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "psql-agent", task: "psql-e2e");
        await RunClientContainer(
            image: "postgres:17",
            scriptFile: "psql-test.sh",
            entrypoint: ["bash", "/scripts/psql-test.sh"],
            token: token);
    }

    [Fact]
    public async Task NodeJs_CanConnectAndQuery()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "node-agent", task: "node-e2e");
        await RunClientContainer(
            image: "node:22-slim",
            scriptFile: "node-test.js",
            entrypoint: ["bash", "-c", "npm install --prefix /tmp pg 2>/dev/null && NODE_PATH=/tmp/node_modules node /scripts/node-test.js"],
            token: token);
    }

    [Fact]
    public async Task Python_CanConnectAndQuery()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "python-agent", task: "python-e2e");
        await RunClientContainer(
            image: "python:3.13-slim",
            scriptFile: "python-test.py",
            entrypoint: ["bash", "-c", "pip install -q 'psycopg[binary]' && python /scripts/python-test.py"],
            token: token);
    }

    [Fact]
    public async Task Go_CanConnectAndQuery()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "go-agent", task: "go-e2e");

        var goDir = new DirectoryInfo(Path.Combine(ScriptsDir, "go-test"));

        var container = new ContainerBuilder("golang:1.25")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithResourceMapping(goDir, "/scripts/")
            .WithEnvironment("PGHOST", "host.docker.internal")
            .WithEnvironment("PGPORT", _fixture.ProxyPort.ToString())
            .WithEnvironment("PGPASSWORD", token)
            .WithEntrypoint("bash", "-c", "cd /scripts && go run .")
            .Build();

        await RunAndAssert(container, "go");
    }

    private async Task RunClientContainer(
        string image, string scriptFile, string[] entrypoint, string token)
    {
        var scriptInfo = new FileInfo(Path.Combine(ScriptsDir, scriptFile));

        var container = new ContainerBuilder(image)
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithResourceMapping(scriptInfo, new FileInfo($"/scripts/{scriptFile}"))
            .WithEnvironment("PGHOST", "host.docker.internal")
            .WithEnvironment("PGPORT", _fixture.ProxyPort.ToString())
            .WithEnvironment("PGPASSWORD", token)
            .WithEntrypoint(entrypoint)
            .Build();

        await RunAndAssert(container, image);
    }

    private static async Task RunAndAssert(IContainer container, string name)
    {
        await container.StartAsync();
        var exitCode = await container.GetExitCodeAsync();
        if (exitCode != 0)
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            await container.DisposeAsync();
            Assert.Fail($"{name} client failed (exit {exitCode}):\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        await container.DisposeAsync();
    }
}
