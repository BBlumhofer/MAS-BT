#!/usr/bin/env python3
"""
MQTT SkillRequest Test Publisher
Sendet eine manuelle SkillRequest Message an den Execution Agent
"""

import json
import time
import paho.mqtt.client as mqtt
from datetime import datetime, timezone
import time
# MQTT Broker Configuration
BROKER_HOST = "localhost"
BROKER_PORT = 1883
TOPIC = "/Modules/CA-Module/SkillRequest/"

# I4.0 Message with Action
def create_skill_request(action_title="Retrieve"):
    """Erstellt eine I4.0 Message mit Action für SkillRequest"""
    
    conversation_id = f"conv_{int(time.time())}"
    
    message = {
        "frame": {
    "sender": {
      "identification": {
        "id": "CA-Module_Planning_Agent"
      },
      "role": {
        "name": "PlanningAgent"
      }
    },
    "receiver": {
      "identification": {
        "id": "CA-Module_Execution_Agent"
      },
      "role": {
        "name": "ExecutionAgent"
      }
    },
    "type": "request",
    "conversationId": conversation_id
  },
  "interactionElements": [
    {
      "idShort": "Action001",
      "kind": "Instance",
      "modelType": "SubmodelElementCollection",
      "semanticId": {
        "type": "ExternalReference",
        "keys": [
          {
            "type": "GlobalReference",
            "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action"
          }
        ]
      },
      "value": [
        {
          "idShort": "ActionTitle",
          "kind": "Instance",
          "modelType": "Property",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/ActionTitle"
              }
            ]
          },
          "valueType": "string",
          "value": "Store"
        },
        {
          "idShort": "Status",
          "kind": "Instance",
          "modelType": "Property",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Status"
              }
            ]
          },
          "valueType": "string",
          "value": "open"
        },
        {
          "idShort": "InputParameters",
          "kind": "Instance",
          "modelType": "SubmodelElementCollection",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/InputParameters"
              }
            ]
          },
          "value": [
            {
              "idShort": "ProductId",
              "kind": "Instance",
              "modelType": "Property",
              "semanticId": {
                "type": "ExternalReference",
                "keys": []
              },
              "valueType": "string",
              "value": "DemoProduct"
            }
          ]
        },
        {
          "idShort": "FinalResultData",
          "kind": "Instance",
          "modelType": "SubmodelElementCollection",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/FinalResultData"
              }
            ]
          },
          "value": []
        },
        {
          "idShort": "Preconditions",
          "kind": "Instance",
          "modelType": "SubmodelElementCollection",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Preconditions"
              }
            ]
          },
          "value": []
        },
        {
          "idShort": "SkillReference",
          "kind": "Instance",
          "modelType": "ReferenceElement",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/SkillReference"
              }
            ]
          },
          "value": {
            "type": "ModelReference",
            "keys": [
              {
                "type": "Submodel",
                "value": "https://example.com/sm"
              }
            ]
          }
        },
        {
          "idShort": "Effects",
          "kind": "Instance",
          "modelType": "SubmodelElementCollection",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/Effecs"
              }
            ]
          },
          "value": []
        },
        {
          "idShort": "MachineName",
          "kind": "Instance",
          "modelType": "Property",
          "semanticId": {
            "type": "ExternalReference",
            "keys": [
              {
                "type": "GlobalReference",
                "value": "https://smartfactory.de/semantics/submodel-element/Step/Actions/Action/MachineName"
              }
            ]
          },
          "valueType": "string",
          "value": "CA-Module"
        }
      ]
    }
  ]
    }
    
    return message




def on_connect(client, userdata, flags, rc):
    """Callback für erfolgreiche MQTT Verbindung"""
    if rc == 0:
        print(f"✅ Verbunden mit MQTT Broker {BROKER_HOST}:{BROKER_PORT}")
        
        # Subscribe zu SkillResponse
        response_topic = "/Modules/Module2/SkillResponse/"
        client.subscribe(response_topic)
        print(f"📡 Subscribed to {response_topic}")
    else:
        print(f"❌ Verbindung fehlgeschlagen: {rc}")


def on_message(client, userdata, msg):
    """Callback für eingehende Messages (SkillResponse)"""
    print(f"\n📨 Empfangene SkillResponse auf {msg.topic}:")
    try:
        response = json.loads(msg.payload.decode())
        
        # Extrahiere ActionState
        elements = response.get("interactionElements", [])
        for element in elements:
            if element.get("idShort") == "ActionResponse":
                values = element.get("value", [])
                for prop in values:
                    if prop.get("idShort") == "ActionState":
                        action_state = prop.get("value")
                        print(f"   ActionState: {action_state}")
                    elif prop.get("idShort") == "ActionTitle":
                        action_title = prop.get("value")
                        print(f"   ActionTitle: {action_title}")
        
        print(f"   Full Response: {json.dumps(response, indent=2)}")
    except Exception as e:
        print(f"   ⚠️  Fehler beim Parsen: {e}")
        print(f"   Raw: {msg.payload.decode()}")


def main():
    print("=" * 70)
    print("🚀 MQTT SkillRequest Test Publisher")
    print("=" * 70)
    
    # Erstelle MQTT Client
    client = mqtt.Client(client_id="SkillRequestTestPublisher")
    client.on_connect = on_connect
    client.on_message = on_message
    
    # Verbinde zum Broker
    print(f"🔌 Verbinde zu MQTT Broker {BROKER_HOST}:{BROKER_PORT}...")
    try:
        client.connect(BROKER_HOST, BROKER_PORT, 60)
    except Exception as e:
        print(f"❌ Fehler beim Verbinden: {e}")
        return
    
    # Starte MQTT Loop (non-blocking)
    client.loop_start()
    
    # Warte kurz auf Verbindung
    time.sleep(2)
    for i in range(5):
        
        # Erstelle und sende SkillRequest
        print(f"\n📤 Sende SkillRequest auf Topic: {TOPIC}")
        
        skill_request = create_skill_request()
        
        payload = json.dumps(skill_request, indent=2)
        print(f"\n📋 Payload:\n{payload}\n")
        
        result = client.publish(TOPIC, payload, qos=1)
        
        if result.rc == mqtt.MQTT_ERR_SUCCESS:
            print(f"✅ SkillRequest {i} erfolgreich gesendet!")
        else:
            print(f"❌ Fehler beim Senden: {result.rc}")
        time.sleep(0.1)
    
    # Warte auf Responses
    print(f"\n⏳ Warte auf SkillResponse vom Execution Agent...")
    print(f"   (Drücke Ctrl+C zum Beenden)\n")
    
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print(f"\n\n🛑 Test beendet")
    
    # Cleanup
    client.loop_stop()
    client.disconnect()


if __name__ == "__main__":
    main()
