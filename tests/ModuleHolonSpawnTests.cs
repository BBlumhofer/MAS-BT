using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Nodes.Configuration;
using MAS_BT.Nodes.ModuleHolon;
using MAS_BT.Services;
using MAS_BT.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using BaSyx.Models.AdminShell;
using Xunit;

namespace MAS_BT.Tests;

public class ModuleHolonSpawnTests
{
    [Fact]
    public async Task SpawnAndRegistersSubHolonsFromConfig()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = "P102",
            AgentRole = "ModuleHolon"
        };

        var configPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../configs/specific_configs/Module_configs/P102/P102.json"));
        var readConfig = new ReadConfigNode { ConfigPath = configPath };
        readConfig.Context = context;
        readConfig.SetLogger(loggerFactory.CreateLogger("ReadConfig"));

        Assert.Equal(NodeStatus.Success, await readConfig.Execute());

        var client = await MAS_BT.Tests.TestHelpers.TestTransportFactory.CreateClientAsync("test/default", "moduleholon-spawn");
        context.Set("MessagingClient", client);

        var launcher = new TestSubHolonLauncher(client);
        context.Set("SubHolonLauncher", launcher);

        var waitNode = new WaitForRegistrationNode { Context = context };
        waitNode.SetLogger(loggerFactory.CreateLogger("WaitForRegistration"));

        var spawnNode = new SpawnSubHolonsNode { Context = context };
        spawnNode.SetLogger(loggerFactory.CreateLogger("SpawnSubHolons"));

        var waitTask = waitNode.Execute();
        await Task.Delay(50); // allow subscription setup
        var spawnStatus = await spawnNode.Execute();
        var waitStatus = await waitTask;

        Assert.Equal(NodeStatus.Success, spawnStatus);
        Assert.Equal(NodeStatus.Success, waitStatus);
        Assert.Equal(2, launcher.LaunchedSpecs.Count);
    }
}

internal class TestSubHolonLauncher : ISubHolonLauncher
{
    private readonly MessagingClient _client;
    public List<SubHolonLaunchSpec> LaunchedSpecs { get; } = new();

    public TestSubHolonLauncher(MessagingClient client)
    {
        _client = client;
    }

    public async Task LaunchAsync(SubHolonLaunchSpec spec, CancellationToken cancellationToken = default)
    {
        LaunchedSpecs.Add(spec);

        var roleName = spec.ConfigPath.Contains("Planning", StringComparison.OrdinalIgnoreCase)
            ? "PlanningHolon"
            : spec.ConfigPath.Contains("Execution", StringComparison.OrdinalIgnoreCase)
                ? "ExecutionHolon"
                : "SubHolonTest";

        var builder = new I40MessageBuilder()
            .From(spec.AgentId ?? $"{spec.ModuleId}_SubHolon", roleName)
            .To($"{spec.ModuleId}_ModuleHolon", null)
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString());

        builder.AddElement(new Property<string>("ModuleId") { Value = new PropertyValue<string>(spec.ModuleId) });

        var message = builder.Build();
        await _client.PublishAsync(message, $"/_PHUKET/{spec.ModuleId}/register", cancellationToken);
    }
}
