using I40Sharp.Messaging.Core;
using BaSyx.Models.AdminShell;
using Xunit;

namespace MAS_BT.Tests;

public class MqttPublishedCalcSimilarityPayloadDeserializationTests
{
    [Fact]
    public void Deserialize_MosquittoPubPayload_CreatesI40MessageWithInteractionElements()
    {
        // This payload matches what we publish manually via mosquitto_pub in dev.
        var json = """
        {
          "frame": {
            "sender": {"identification": {"id": "DispatchingAgent_phuket"}, "role": {"name": "DispatchingAgent"}},
            "receiver": {"identification": {"id": "SimilarityAnalysisAgent_phuket"}, "role": {"name": "AIAgent"}},
            "type": "calcSimilarity",
            "conversationId": "test-12345"
          },
          "interactionElements": [
            {"idShort": "Capability_0", "kind": "Instance", "modelType": "Property", "valueType": "string", "value": "Assemble"},
            {"idShort": "Capability_1", "kind": "Instance", "modelType": "Property", "valueType": "string", "value": "Screw"}
          ]
        }
        """;

        var serializer = new MessageSerializer();
        var msg = serializer.Deserialize(json);

        Assert.NotNull(msg);
        Assert.Equal("calcSimilarity", msg!.Frame.Type);
        Assert.Equal("test-12345", msg.Frame.ConversationId);
        Assert.Equal("DispatchingAgent_phuket", msg.Frame.Sender.Identification.Id);
        Assert.Equal("SimilarityAnalysisAgent_phuket", msg.Frame.Receiver.Identification.Id);

        Assert.NotNull(msg.InteractionElements);
        Assert.Equal(2, msg.InteractionElements.Count);
        Assert.Equal("Capability_0", msg.InteractionElements[0].IdShort);
        Assert.Equal("Capability_1", msg.InteractionElements[1].IdShort);

        // Ensure the simplified `value` field was mapped into a BaSyx PropertyValue
        var p0 = Assert.IsType<Property>(msg.InteractionElements[0]);
        var p1 = Assert.IsType<Property>(msg.InteractionElements[1]);

        // CalcEmbedding uses reflection to read Value.Value; validate that path exists.
        static object? GetParameterlessProperty(object obj, string name)
        {
          var props = obj.GetType().GetProperties()
            .Where(p => string.Equals(p.Name, name, StringComparison.Ordinal) && p.GetIndexParameters().Length == 0)
            .ToArray();

          return props.Length == 1 ? props[0].GetValue(obj) : props.FirstOrDefault()?.GetValue(obj);
        }

        var v0 = GetParameterlessProperty(p0, "Value");
        var inner0 = v0 is null ? null : GetParameterlessProperty(v0, "Value")?.ToString();
        var v1 = GetParameterlessProperty(p1, "Value");
        var inner1 = v1 is null ? null : GetParameterlessProperty(v1, "Value")?.ToString();

        Assert.Equal("Assemble", inner0);
        Assert.Equal("Screw", inner1);
    }
}
