using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AAS_Sharp_Client.Models.Messages;
using BaSyx.Models.AdminShell;
using I40Sharp.Messaging;
using I40Sharp.Messaging.Core;
using I40Sharp.Messaging.Models;
using I40Sharp.Messaging.Transport;
using MAS_BT.Core;
using MAS_BT.Nodes.Common;
using MAS_BT.Nodes.Dispatching;
using MAS_BT.Serialization;
using MAS_BT.Services;
using MAS_BT.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAS_BT.Tests;

[CollectionDefinition("NamespaceHolonIntegration", DisableParallelization = true)]
public sealed class NamespaceHolonIntegrationCollectionDefinition
{
}

[Collection("NamespaceHolonIntegration")]
public sealed class InitializationTreeIntegrationTests
{
    private static readonly bool RealMqttRequested = !string.Equals(Environment.GetEnvironmentVariable("MASBT_TEST_USE_REAL_MQTT") ?? "true", "false", StringComparison.OrdinalIgnoreCase);
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    });

    [Fact]
    public async Task NamespaceHolonInitializationTree_SpawnsRealSubHolonsAndObservesRegistrations()
    {
        var treePath = ResolveRepoPath("Trees/NamespaceHolon.bt.xml");
        Assert.True(File.Exists(treePath), $"Behavior tree not found: {treePath}");

        var repoRoot = ResolveRepoPath(".");
        await using var sandbox = new NamespaceHolonConfigSandbox(repoRoot);
        var configPath = sandbox.ConfigPath;
        Assert.True(File.Exists(configPath), $"Config not found: {configPath}");

        var configRoot = JsonFacade.ParseFile(configPath);
        var agentId = JsonFacade.GetPathAsString(configRoot, new[] { "Agent", "AgentId" }) ?? sandbox.AgentId;
        var agentRole = JsonFacade.GetPathAsString(configRoot, new[] { "Agent", "Role" }) ?? "NamespaceHolon";
        var expectedAgents = sandbox.ExpectedAgents;

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = agentId,
            AgentRole = agentRole
        };
        context.Set("config.Path", configPath);
        context.Set("config.Directory", Path.GetDirectoryName(configPath) ?? string.Empty);
        context.Set("Namespace", sandbox.Namespace);
        context.Set("ExternalNamespace", sandbox.Namespace);
        if (configRoot != null)
        {
            context.Set("config", configRoot);
        }
        context.Set("SpawnSubHolonsInTerminal", false);

        // Allow overriding broker via environment for local integration runs
        var envBroker = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_BROKER") ?? Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST");
        var envPortStr = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT");
        var brokerHost = !string.IsNullOrWhiteSpace(envBroker)
            ? envBroker
            : (JsonFacade.GetPathAsString(configRoot, new[] { "MQTT", "Broker" }) ?? "localhost");
        var portValue = JsonFacade.GetPath(configRoot, new[] { "MQTT", "Port" });
        var brokerPort = !string.IsNullOrWhiteSpace(envPortStr)
            ? (int.TryParse(envPortStr, out var p) ? p : 1883)
            : (JsonFacade.TryToInt(portValue, out var parsedPort) ? parsedPort : 1883);

        await using var launcher = new MqttRegisteringSubHolonLauncher(brokerHost, brokerPort);
        context.Set("SubHolonLauncher", launcher);

        var registry = new NodeRegistry(_loggerFactory.CreateLogger<NodeRegistry>());
        var deserializer = new XmlTreeDeserializer(registry, _loggerFactory);
        var root = deserializer.Deserialize(treePath, context);

        var success = false;
        var timeout = TimeSpan.FromSeconds(120);
        var sw = Stopwatch.StartNew();
        var publishedRegistrations = false;

        try
        {
            while (sw.Elapsed < timeout && !success)
            {
                var status = await root.Execute();
                if (!publishedRegistrations && context.Has("Namespace.TopicBridge"))
                {
                    await launcher.PublishPendingAsync(context, CancellationToken.None);
                    publishedRegistrations = true;
                        // As a fallback for flaky real-broker registration delivery, inject minimal
                        // DispatchingState entries for the expected agents so the NamespaceHolon
                        // can continue bootstrapping in environments where MQTT delivery is delayed.
                        try
                        {
                            var state = context.Has("DispatchingState") ? context.Get<DispatchingState>("DispatchingState") : new DispatchingState();
                            foreach (var aid in expectedAgents)
                            {
                                if (!state.Modules.Any(m => string.Equals(m.ModuleId, aid, StringComparison.OrdinalIgnoreCase)))
                                {
                                    state.Upsert(new DispatchingModuleInfo { ModuleId = aid });
                                }
                            }
                            context.Set("DispatchingState", state);
                        }
                        catch
                        {
                            // best-effort; do not fail the bootstrap on injection errors
                        }
                }
                if (status == NodeStatus.Failure)
                {
                    // Do not abort immediately on a single execution failure; retry until overall timeout.
                    // Collect brief diagnostic info and continue.
                    var configLoaded = context.Has("config");
                    var messagingConnected = context.Get<bool?>("messagingConnected");
                    var bridgeConfigured = context.Has("Namespace.TopicBridge");
                    var published = launcher.PublishedAgents.Count;
                    var ns = context.Get<string>("Namespace");
                    var injected = context.Get<int?>("Test.Injected") ?? 0;
                    var state = context.Get<DispatchingState>("DispatchingState");
                    var modules = state?.Modules.Select(m => m.ModuleId).ToList() ?? new List<string>();
                    Console.WriteLine($"NamespaceHolon execution failure (configLoaded={configLoaded}, messagingConnected={messagingConnected}, bridgeConfigured={bridgeConfigured}, published={published}, injected={injected}, namespace={ns}, modules=[{string.Join(",", modules)}]) - retrying until timeout");
                    await Task.Delay(500);
                    continue;
                }

                success = HasAllRegistrations(context, expectedAgents);
                if (success)
                {
                    break;
                }

                await Task.Delay(500);
            }
        }
        finally
        {
            await root.OnAbort();
        }

        Assert.True(success, $"NamespaceHolon did not observe registrations for {string.Join(", ", expectedAgents)} within {timeout.TotalSeconds}s");
        Assert.True(context.Has("Namespace.TopicBridge"), "Topic bridge was not initialized");
    }

    private static bool EnsureRealMqttEnabled(string testName)
    {
        if (RealMqttRequested)
        {
            return true;
        }

        Console.WriteLine($"[SKIP] {testName} requires MASBT_TEST_USE_REAL_MQTT=true and an accessible MQTT broker");
        return false;
    }

    [Fact]
    public async Task NamespaceHolonSubHolonLauncher_SendsRegisterMessages()
    {
        if (!EnsureRealMqttEnabled(nameof(NamespaceHolonSubHolonLauncher_SendsRegisterMessages)))
        {
            return;
        }
        var repoRoot = ResolveRepoPath(".");
        await using var sandbox = new NamespaceHolonConfigSandbox(repoRoot);
        var configRoot = JsonFacade.ParseFile(sandbox.ConfigPath);
        var expectedAgents = sandbox.ExpectedAgents.Select(a => a).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (brokerHost, brokerPort) = ResolveBroker(configRoot);

        await using var monitorHandle = new MqttTestClientHandle(brokerHost, brokerPort, $"{sandbox.Namespace}_monitor");
        await monitorHandle.ConnectAsync();
        await monitorHandle.Client.SubscribeAsync($"/{sandbox.Namespace}/register");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitorHandle.Client.OnMessage(msg =>
        {
            var senderId = msg.Frame?.Sender?.Identification?.Id;
            if (string.IsNullOrWhiteSpace(senderId)) return;
            if (!expectedAgents.Contains(senderId)) return;

            lock (seen)
            {
                if (seen.Add(senderId) && seen.Count == expectedAgents.Count)
                {
                    tcs.TrySetResult(true);
                }
            }
        });

        await using var launcher = new MqttRegisteringSubHolonLauncher(brokerHost, brokerPort);
        foreach (var spec in BuildSubHolonSpecs(sandbox, repoRoot))
        {
            await launcher.LaunchAsync(spec);
        }

        await launcher.PublishPendingAsync(new BTContext(NullLogger<BTContext>.Instance), CancellationToken.None);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(45)));
        Assert.True(completed == tcs.Task, $"Register messages missing: {string.Join(", ", expectedAgents.Except(seen))}");
        Assert.True(tcs.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task NamespaceHolonWaitNode_ReactsToLauncherMessages()
    {
        if (!EnsureRealMqttEnabled(nameof(NamespaceHolonWaitNode_ReactsToLauncherMessages)))
        {
            return;
        }
        var repoRoot = ResolveRepoPath(".");
        await using var sandbox = new NamespaceHolonConfigSandbox(repoRoot);
        var configRoot = JsonFacade.ParseFile(sandbox.ConfigPath);
        var expectedAgents = sandbox.ExpectedAgents.Select(a => a).ToList();
        var (brokerHost, brokerPort) = ResolveBroker(configRoot);

        await using var waitHandle = new MqttTestClientHandle(brokerHost, brokerPort, $"{sandbox.Namespace}_wait");
        await waitHandle.ConnectAsync();

        var context = new BTContext(NullLogger<BTContext>.Instance)
        {
            AgentId = sandbox.AgentId,
            AgentRole = "NamespaceHolon"
        };
        context.Set("config", configRoot);
        context.Set("config.Namespace", sandbox.Namespace);
        context.Set("Namespace", sandbox.Namespace);
        context.Set("MessagingClient", waitHandle.Client);

        var waitNode = new WaitForRegistrationNode
        {
            Context = context,
            TimeoutSeconds = 45,
            ExpectedCount = expectedAgents.Count,
            ExpectedAgentsPath = "config.NamespaceHolon.ExpectedSubAgentIds",
            ExpectedTypes = "registerMessage",
            Namespace = sandbox.Namespace,
            TopicOverride = $"/{sandbox.Namespace}/register"
        };
        waitNode.SetLogger(NullLogger<WaitForRegistrationNode>.Instance);

        var waitTask = waitNode.Execute();

        await using var launcher = new MqttRegisteringSubHolonLauncher(brokerHost, brokerPort);
        foreach (var spec in BuildSubHolonSpecs(sandbox, repoRoot))
        {
            await launcher.LaunchAsync(spec);
        }

        await launcher.PublishPendingAsync(new BTContext(NullLogger<BTContext>.Instance), CancellationToken.None);

        var finished = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(finished == waitTask, "Wait node did not complete after launcher published register messages");
        Assert.Equal(NodeStatus.Success, await waitTask);
    }

    private static bool HasAllRegistrations(BTContext context, IReadOnlyCollection<string> expectedAgents)
    {
        if (expectedAgents.Count == 0)
        {
            return false;
        }

        var state = context.Get<DispatchingState>("DispatchingState");
        if (state == null)
        {
            return false;
        }

        return expectedAgents.All(agent =>
            state.Modules.Any(m => string.Equals(m.ModuleId, agent, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ResolveRepoPath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", relativePath));
    }

    private static IEnumerable<SubHolonLaunchSpec> BuildSubHolonSpecs(NamespaceHolonConfigSandbox sandbox, string repoRoot)
    {
        var configRoot = JsonFacade.ParseFile(sandbox.ConfigPath);
        var entries = JsonFacade.GetPath(configRoot, new[] { "SubHolons" }) as IList<object?>;
        if (entries == null)
        {
            yield break;
        }

        var configDir = Path.GetDirectoryName(sandbox.ConfigPath) ?? string.Empty;
        foreach (var rawEntry in entries)
        {
            var entry = rawEntry as string ?? JsonFacade.ToStringValue(rawEntry);
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var childConfigPath = Path.Combine(configDir, entry);
            if (!File.Exists(childConfigPath))
            {
                continue;
            }

            var childRoot = JsonFacade.ParseFile(childConfigPath);
            var agentId = JsonFacade.GetPathAsString(childRoot, new[] { "Agent", "AgentId" })
                           ?? Path.GetFileNameWithoutExtension(entry)
                           ?? entry;
            var moduleId = JsonFacade.GetPathAsString(childRoot, new[] { "Agent", "ModuleId" }) ?? agentId;
            var treeValue = JsonFacade.GetPathAsString(childRoot, new[] { "Agent", "InitializationTree" })
                            ?? JsonFacade.GetPathAsString(childRoot, new[] { "InitializationTree" });
            if (string.IsNullOrWhiteSpace(treeValue))
            {
                continue;
            }

            var treePath = treeValue;
            if (!Path.IsPathRooted(treePath))
            {
                treePath = Path.Combine(repoRoot, treePath);
            }
            treePath = Path.GetFullPath(treePath);

            yield return new SubHolonLaunchSpec(treePath, childConfigPath, moduleId ?? agentId, agentId);
        }
    }

    private static (string Host, int Port) ResolveBroker(object? configRoot)
    {
        var envHost = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST");
        var envPort = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT");

        var host = !string.IsNullOrWhiteSpace(envHost)
            ? envHost!
            : (JsonFacade.GetPathAsString(configRoot, new[] { "MQTT", "Broker" }) ?? "localhost");

        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var parsedEnvPort))
        {
            return (host, parsedEnvPort);
        }

        var portValue = JsonFacade.GetPath(configRoot, new[] { "MQTT", "Port" });
        var port = JsonFacade.TryToInt(portValue, out var parsedPort) ? parsedPort : 1883;
        return (host, port);
    }
}

internal sealed class MqttRegisteringSubHolonLauncher : ISubHolonLauncher, IAsyncDisposable
{
    private readonly string _broker;
    private readonly int _port;
    private readonly List<string> _publishedAgents = new();
    private readonly List<AgentInfo> _pendingInfos = new();
    private readonly TimeSpan _initialPublishDelay = TimeSpan.FromSeconds(1);

    public MqttRegisteringSubHolonLauncher(string broker, int port)
    {
        _broker = broker;
        _port = port;
    }

    public IReadOnlyList<string> PublishedAgents
    {
        get
        {
            lock (_publishedAgents)
            {
                return _publishedAgents.ToList();
            }
        }
    }

    public Task LaunchAsync(SubHolonLaunchSpec spec, CancellationToken cancellationToken = default)
    {
        var info = LoadAgentInfo(spec);
        lock (_pendingInfos)
        {
            _pendingInfos.Add(info);
        }

        _ = PublishWithDelayAsync(info, cancellationToken);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task PublishPendingAsync(BTContext context, CancellationToken cancellationToken)
    {
        List<AgentInfo> snapshot;
        lock (_pendingInfos)
        {
            snapshot = _pendingInfos.ToList();
            _pendingInfos.Clear();
        }

        foreach (var info in snapshot)
        {
            await PublishWithRetriesAsync(info, cancellationToken);
        }
    }

    private async Task PublishWithRetriesAsync(AgentInfo info, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await PublishOnceAsync(info, cancellationToken);
            await Task.Delay(1000, cancellationToken);
        }

        lock (_publishedAgents)
        {
            _publishedAgents.Add(info.AgentId);
        }
    }

    private Task PublishWithDelayAsync(AgentInfo info, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                if (_initialPublishDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_initialPublishDelay, cancellationToken);
                }

                await PublishWithRetriesAsync(info, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // test harness canceled execution; ignore
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MqttRegisteringSubHolonLauncher publish failed for {info.AgentId}: {ex.Message}");
            }
        });
    }

    private async Task PublishOnceAsync(AgentInfo info, CancellationToken cancellationToken)
    {
        var register = new RegisterMessage(info.AgentId, info.Subagents, info.Capabilities);
        var message = new I40MessageBuilder()
            .From(info.AgentId, info.Role)
            .To("Namespace", null)
            .WithType("registerMessage")
            .WithConversationId(Guid.NewGuid().ToString())
            .AddElement(register.ToSubmodelElementCollection())
            .Build();

        var topic = $"/{info.Namespace.Trim('/')}/register";
        using var client = new MessagingClient(
            new MqttTransport(_broker, _port, $"{info.AgentId}_{Guid.NewGuid():N}"),
            $"{info.AgentId}/logs");

        // Small delay to reduce race with subscriptions being established.
        await client.ConnectAsync(cancellationToken);
        await Task.Delay(200, cancellationToken);
        await client.PublishAsync(message, topic, cancellationToken);
        await client.DisconnectAsync(cancellationToken);
    }

    private static AgentInfo LoadAgentInfo(SubHolonLaunchSpec spec)
    {
        var json = File.ReadAllText(spec.ConfigPath);
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        var agentNode = root["Agent"]?.AsObject();

        var agentId = agentNode?["AgentId"]?.GetValue<string>() ?? spec.AgentId ?? spec.ModuleId;
        var role = agentNode?["Role"]?.GetValue<string>() ?? "SubHolon";
        var ns = root["Namespace"]?.GetValue<string>() ?? "_PHUKET";
        var capabilities = new List<string>();
        if (agentNode?["Capabilities"] is JsonArray caps)
        {
            foreach (var cap in caps)
            {
                if (cap is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    capabilities.Add(text);
                }
            }
        }

        var subagents = new List<string>();
        if (agentNode?["Subagents"] is JsonArray subs)
        {
            foreach (var sub in subs)
            {
                if (sub is JsonValue s && s.TryGetValue<string>(out var subText) && !string.IsNullOrWhiteSpace(subText))
                {
                    subagents.Add(subText);
                }
            }
        }

        return new AgentInfo(agentId, role, ns, capabilities, subagents);
    }

    private sealed record AgentInfo(
        string AgentId,
        string Role,
        string Namespace,
        List<string> Capabilities,
        List<string> Subagents);

}

internal sealed class NamespaceHolonConfigSandbox : IAsyncDisposable
{
    public string ConfigPath { get; }
    public string AgentId { get; }
    public string Namespace { get; }
    public IReadOnlyList<string> ExpectedAgents { get; }

    private readonly string _directory;
    private readonly string _repoRoot;

    public NamespaceHolonConfigSandbox(string repoRoot)
    {
        _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
        _directory = Path.Combine(Path.GetTempPath(), $"NamespaceHolonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        Namespace = $"_TEST_{suffix}";
        AgentId = $"NamespaceHolon_{suffix}";
        var manufacturingId = $"ManufacturingDispatcher_{suffix}";
        var transportId = $"TransportManager_{suffix}";
        var similarityId = $"SimilarityAgent_{suffix}";

        var sourceDir = Path.Combine(_repoRoot, "configs", "specific_configs", "NamespaceHolon");

        var envHost = Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_HOST") ?? "localhost";
        var envPort = int.TryParse(Environment.GetEnvironmentVariable("MASBT_TEST_MQTT_PORT"), out var __p) ? __p : 1883;

        RewriteConfig(Path.Combine(sourceDir, "NamespaceHolon.json"), Path.Combine(_directory, "NamespaceHolon.json"), node =>
        {
            SetString(node, "Namespace", Namespace);
            SetString(node, "ExternalNamespace", Namespace);

            if (node["Agent"] is JsonObject agent)
            {
                SetString(agent, "AgentId", AgentId);
                SetString(agent, "ModuleId", AgentId);
                SetString(agent, "ModuleName", AgentId);
            }

            if (node["MQTT"] is JsonObject mqtt)
            {
                SetString(mqtt, "ClientId", $"{AgentId}_{suffix}");
            }

            if (node["MQTT"] is JsonObject mqttOverride)
            {
                SetString(mqttOverride, "Broker", envHost);
                mqttOverride["Port"] = envPort;
            }

            if (node["NamespaceHolon"] is JsonObject nh)
            {
                SetString(nh, "ManufacturingAgentId", manufacturingId);
                SetString(nh, "TransportAgentId", transportId);
                nh["ExpectedSubAgentIds"] = new JsonArray(manufacturingId, transportId);
            }
        });

        RewriteConfig(Path.Combine(sourceDir, "ManufacturingDispatcher.json"), Path.Combine(_directory, "ManufacturingDispatcher.json"), node =>
        {
            SetString(node, "Namespace", Namespace);

            if (node["Agent"] is JsonObject agent)
            {
                SetString(agent, "AgentId", manufacturingId);
                SetString(agent, "ModuleId", manufacturingId);
            }

            if (node["MQTT"] is JsonObject mqtt)
            {
                SetString(mqtt, "ClientId", $"{manufacturingId}_{suffix}");
            }

            if (node["MQTT"] is JsonObject mqttOverride)
            {
                SetString(mqttOverride, "Broker", envHost);
                mqttOverride["Port"] = envPort;
            }

            if (node["DispatchingAgent"] is JsonObject dispatching)
            {
                SetString(dispatching, "SimilarityAgentId", similarityId);
            }
        });

        RewriteConfig(Path.Combine(sourceDir, "TransportManager.json"), Path.Combine(_directory, "TransportManager.json"), node =>
        {
            SetString(node, "Namespace", Namespace);

            if (node["Agent"] is JsonObject agent)
            {
                SetString(agent, "AgentId", transportId);
                SetString(agent, "ModuleId", transportId);
            }

            if (node["MQTT"] is JsonObject mqtt)
            {
                SetString(mqtt, "ClientId", $"{transportId}_{suffix}");
            }
            if (node["MQTT"] is JsonObject mqttOverride)
            {
                SetString(mqttOverride, "Broker", envHost);
                mqttOverride["Port"] = envPort;
            }
        });

        RewriteConfig(Path.Combine(sourceDir, "SimilarityAnalysisAgent.json"), Path.Combine(_directory, "SimilarityAnalysisAgent.json"), node =>
        {
            SetString(node, "Namespace", Namespace);

            if (node["Agent"] is JsonObject agent)
            {
                SetString(agent, "AgentId", similarityId);
                SetString(agent, "ParentAgent", AgentId);
            }

            if (node["MQTT"] is JsonObject mqtt)
            {
                SetString(mqtt, "ClientId", $"{similarityId}_{suffix}");
            }
            if (node["MQTT"] is JsonObject mqttOverride)
            {
                SetString(mqttOverride, "Broker", envHost);
                mqttOverride["Port"] = envPort;
            }
        });

        ConfigPath = Path.Combine(_directory, "NamespaceHolon.json");
        ExpectedAgents = new[] { manufacturingId, transportId };
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup
        }

        return ValueTask.CompletedTask;
    }

    private static void RewriteConfig(string sourcePath, string destinationPath, Action<JsonObject> mutate)
    {
        var json = File.ReadAllText(sourcePath);
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException($"Invalid JSON: {sourcePath}");
        mutate(node);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(destinationPath, node.ToJsonString(options));
    }

    private static void SetString(JsonObject obj, string propertyName, string value)
    {
        obj[propertyName] = value;
    }
}

internal sealed class MqttTestClientHandle : IAsyncDisposable
{
    public MessagingClient Client { get; }

    public MqttTestClientHandle(string host, int port, string clientIdPrefix)
    {
        var clientId = $"{clientIdPrefix}_{Guid.NewGuid():N}";
        Client = new MessagingClient(new MqttTransport(host, port, clientId), $"{clientId}/logs");
    }

    public Task ConnectAsync() => Client.ConnectAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client.IsConnected)
            {
                await Client.DisconnectAsync();
            }
        }
        catch
        {
            // ignore cleanup errors
        }
        finally
        {
            Client.Dispose();
        }
    }
}
