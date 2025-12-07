#!/usr/bin/env python3
"""
MQTT SkillRequest Test Publisher
Sendet eine manuelle SkillRequest Message an den Execution Agent
"""

import json
import time
import paho.mqtt.client as mqtt
from datetime import datetime, timezone

# MQTT Broker Configuration
BROKER_HOST = "172.24.100.85"
BROKER_PORT = 1883
TOPIC = "/Modules/CA-Module/SkillRequest/"

# I4.0 Message with Action
def create_skill_request(action_title="Retrieve"):
    """Erstellt eine I4.0 Message mit Action für SkillRequest"""
    
    conversation_id = f"conv_{int(time.time())}"
    message_id = f"msg_{int(time.time())}"
    
    message = {
        "frame": {
            "sender": {
                "identification": {
                    "id": "Module2_Planning_Agent"
                },
                "role": {
                    "name": "PlanningAgent"
                }
            },
            "receiver": {
                "identification": {
                    "id": "Module2_Execution_Agent"
                },
                "role": {
                    "name": "ExecutionAgent"
                }
            },
            "type": "request",
            "conversationId": conversation_id,
            "messageId": message_id,
            "replyBy": (datetime.now(timezone.utc).isoformat())
        },
        "interactionElements": [
            {
                "idShort": "Action001",
                "modelType": "SubmodelElementCollection",
                "value": [
                    {
                        "idShort": "ActionTitle",
                        "modelType": "Property",
                        "valueType": "xs:string",
                        "value": action_title
                    },
                    {
                        "idShort": "Status",
                        "modelType": "Property",
                        "valueType": "xs:string",
                        "value": "planned"
                    },
                    {
                        "idShort": "MachineName",
                        "modelType": "Property",
                        "valueType": "xs:string",
                        "value": "CA-Module"
                    },
                    {
                        "idShort": "InputParameters",
                        "modelType": "SubmodelElementCollection",
                        "value": [
                            {
                                "idShort": "ProductId",
                                "modelType": "Property",
                                "valueType": "xs:string",
                                "value": "https://smartfactory.de/shells/test_product"
                            },
                            {
                                "idShort": "RetrieveByProductID",
                                "modelType": "Property",
                                "valueType": "xs:boolean",
                                "value": "true"
                            }
                        ]
                    },
                    {
                        "idShort": "Preconditions",
                        "modelType": "SubmodelElementCollection",
                        "value": []
                    },
                    {
                        "idShort": "Effects",
                        "modelType": "SubmodelElementCollection",
                        "value": []
                    },
                    {
                        "idShort": "FinalResultData",
                        "modelType": "SubmodelElementCollection",
                        "value": []
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
    
    # Erstelle und sende SkillRequest
    print(f"\n📤 Sende SkillRequest auf Topic: {TOPIC}")
    
    skill_request = create_skill_request()
    
    payload = json.dumps(skill_request, indent=2)
    print(f"\n📋 Payload:\n{payload}\n")
    
    result = client.publish(TOPIC, payload, qos=1)
    
    if result.rc == mqtt.MQTT_ERR_SUCCESS:
        print(f"✅ SkillRequest erfolgreich gesendet!")
    else:
        print(f"❌ Fehler beim Senden: {result.rc}")
    
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
