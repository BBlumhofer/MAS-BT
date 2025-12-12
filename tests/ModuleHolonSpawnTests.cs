using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using MAS_BT.Core;
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

        var transport = new InMemoryTransport();
        var client = new MessagingClient(transport, "test/default");
        await client.ConnectAsync();
        context.Set("MessagingClient", client);

        var launcher = new TestSubHolonLauncher(client);
        context.Set("SubHolonLauncher", launcher);

        var waitNode = new WaitForSubHolonRegisterNode { Context = context };
        waitNode.SetLogger(loggerFactory.CreateLogger("WaitForSubHolonRegister"));

        var spawnNode = new SpawnSubHolonsNode { Context = context };
        spawnNode.SetLogger(loggerFactory.CreateLogger("SpawnSubHolons"));

        var waitTask = waitNode.Execute();
        await Task.Delay(50); // allow subscription setup
        var spawnStatus = await spawnNode.Execute();
        var waitStatus = await waitTask;

        Assert.Equal(NodeStatus.Success, spawnStatus);
        Assert.Equal(NodeStatus.Success, waitStatus);
        Assert.Equal(1, launcher.LaunchedSpecs.Count);
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

        var builder = new I40MessageBuilder()
            .From(spec.AgentId ?? $"{spec.ModuleId}_SubHolon", "SubHolonTest")
            .To($"{spec.ModuleId}_ModuleHolon", null)
            .WithType("subHolonRegister")
            .WithConversationId(Guid.NewGuid().ToString());

        builder.AddElement(new Property<string>("ModuleId") { Value = new PropertyValue<string>(spec.ModuleId) });

        var message = builder.Build();
        await _client.PublishAsync(message, $"/phuket/{spec.ModuleId}/register", cancellationToken);
    }
}
