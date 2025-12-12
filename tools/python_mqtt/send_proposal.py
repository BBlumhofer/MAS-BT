#!/usr/bin/env python3
"""Simple MQTT publisher for testing the MAS-BT negotiation.

Sends an I4.0-style message with a `frame` object containing
`conversationId`, `type` and `sender`. Use `--type` to choose
between `proposal` and `refuseProposal`.

Dependencies:
  pip install paho-mqtt

Example:
  python3 tools/send_proposal.py --conversation-id 71e95af0-... \
    --type proposal
"""
import argparse
import json
import sys
from datetime import datetime, timezone

try:
    import paho.mqtt.client as mqtt
except Exception as e:
    print("Missing dependency: paho-mqtt.", file=sys.stderr)
    print(f"This Python interpreter is: {sys.executable}", file=sys.stderr)
    print("Install with:", file=sys.stderr)
    print(f"  {sys.executable} -m pip install --user paho-mqtt", file=sys.stderr)
    print("or", file=sys.stderr)
    print("  python3 -m pip install --user paho-mqtt", file=sys.stderr)
    print(f"(original error: {e})", file=sys.stderr)
    sys.exit(2)


def build_message(conversation_id: str, msg_type: str, sender_id: str, extra_payload: dict | None, receiver_id: str = None):
    frame = {
        "conversationId": conversation_id,
        "type": msg_type,
        "sender": {"id": sender_id},
        "timestamp": datetime.now(timezone.utc).isoformat(),
    }

    if receiver_id:
        frame["receiver"] = {"id": receiver_id}

    msg = {
        "frame": frame,
        "interactionElements": [],
    }

    if extra_payload:
        # attach under a single interaction element for simplicity
        msg["interactionElements"].append({"value": extra_payload})

    return msg


def publish(broker, port, topic, payload, qos=1, retain=False, timeout=5):
    client = mqtt.Client()

    connected = False

    def on_connect(client, userdata, flags, rc):
        nonlocal connected
        if rc == 0:
            connected = True
            print(f"Connected to MQTT broker {broker}:{port}")
        else:
            print(f"Failed to connect, rc={rc}", file=sys.stderr)

    client.on_connect = on_connect

    try:
        client.connect(broker, port, keepalive=60)
        client.loop_start()

        # wait for connection
        import time

        waited = 0.0
        while not connected and waited < timeout:
            time.sleep(0.1)
            waited += 0.1

        if not connected:
            client.loop_stop()
            print("Could not connect to MQTT broker within timeout", file=sys.stderr)
            return 2

        info = client.publish(topic, payload, qos=qos, retain=retain)
        info.wait_for_publish()
        print(f"Published to topic {topic} (qos={qos}, retain={retain})")

        client.disconnect()
        client.loop_stop()
        return 0
    except Exception as e:
        print(f"MQTT error: {e}", file=sys.stderr)
        return 3


def main():
    parser = argparse.ArgumentParser(description="Send a proposal or refusal for testing MAS-BT agents")
    parser.add_argument("--broker", default="192.168.178.33", help="MQTT broker host (default: localhost)")
    parser.add_argument("--port", type=int, default=1883, help="MQTT broker port (default: 1883)")
    parser.add_argument("--topic", default="/phuket/ProcessChain", help="MQTT topic to publish to")
    parser.add_argument("--type", choices=["proposal", "refuseProposal", "refusal"], default="proposal", help="Message type to send (alias: 'refusal' -> 'refuseProposal')")
    parser.add_argument("--conversation-id", required=True, help="ConversationId to use (use the one logged by the agent)")
    parser.add_argument("--sender", default="ManualTester", help="Sender id to include in the frame")
    parser.add_argument("--receiver", default="Broadcast", help="Receiver id to include in the frame (default: Broadcast)")
    parser.add_argument("--qos", type=int, default=1, help="MQTT QoS level")
    parser.add_argument("--retain", action="store_true", help="Set MQTT retain flag")
    parser.add_argument("--json", help="Optional extra JSON payload to include as interactionElement")

    args = parser.parse_args()

    extra = None
    if args.json:
        try:
            extra = json.loads(args.json)
        except Exception as e:
            print(f"Failed to parse --json payload: {e}", file=sys.stderr)
            sys.exit(4)

    # accept alias 'refusal' for 'refuseProposal'
    msg_type = args.type
    if msg_type == "refusal":
        msg_type = "refuseProposal"

    message = build_message(args.conversation_id, msg_type, args.sender, extra, receiver_id=args.receiver)
    payload = json.dumps(message)

    print("Message to publish:")
    print(payload)

    rc = publish(args.broker, args.port, args.topic, payload, qos=args.qos, retain=args.retain)
    sys.exit(rc)


if __name__ == "__main__":
    main()
