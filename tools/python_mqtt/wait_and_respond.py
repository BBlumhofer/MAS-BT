#!/usr/bin/env python3
"""Wait for a callForProposal from the agent and reply with proposal or refuseProposal.

Usage:
  python3 tools/wait_and_respond.py --respond-type proposal

This script subscribes to `/{namespace}/request/ProcessChain`, waits for a message
with type `callForProposal`, extracts the `conversationId` and publishes a response
to `/{namespace}/response/ProcessChain` with the same conversationId.

Dependencies: paho-mqtt
"""
import argparse
import json
import sys
import time
from datetime import datetime, timezone
import re

try:
    import paho.mqtt.client as mqtt
except Exception as e:
    print("Missing dependency: paho-mqtt.")
    print(f"Install with: {sys.executable} -m pip install --user paho-mqtt")
    sys.exit(2)


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def make_response(conversation_id, msg_type, sender_id, receiver_id=None, extra=None):
    frame = {
        "conversationId": conversation_id,
        "type": msg_type,
        "sender": {"id": sender_id},
        "timestamp": now_iso(),
    }
    if receiver_id:
        frame["receiver"] = {"id": receiver_id}

    msg = {"frame": frame, "interactionElements": []}
    if extra:
        msg["interactionElements"].append({"value": extra})
    return json.dumps(msg)


def main():
    parser = argparse.ArgumentParser(description="Wait for callForProposal and respond")
    parser.add_argument("--broker", default="localhost")
    parser.add_argument("--port", type=int, default=1883)
    parser.add_argument("--namespace", default="phuket")
    parser.add_argument("--respond-type", choices=["proposal", "refuseProposal", "refusal"], default="proposal")
    parser.add_argument("--sender", default="TestResponder")
    parser.add_argument("--receiver", default=None, help="Optional receiver id to include in response")
    parser.add_argument("--timeout", type=int, default=30, help="Seconds to wait for callForProposal")
    parser.add_argument("--initial-delay", type=int, default=10, help="Seconds to wait before subscribing (give agent time to init)")
    args = parser.parse_args()

    if args.respond_type == "refusal":
        args.respond_type = "refuseProposal"

    req_topic = f"/{args.namespace}/request/ProcessChain"
    resp_topic = f"/{args.namespace}/response/ProcessChain"

    received = None

    client = mqtt.Client()

    def on_connect(c, userdata, flags, rc):
        print(f"Connected to broker {args.broker}:{args.port} (rc={rc})")
        c.subscribe(req_topic)
        print(f"Subscribed to {req_topic}")

    def on_message(c, userdata, msg):
        nonlocal received
        try:
            payload = msg.payload.decode('utf-8')
            data = json.loads(payload)
            frame = data.get('frame', {})
            mtype = frame.get('type')
            # Try to extract the exact conversationId string from the raw payload
            # (so we preserve any formatting/characters exactly as published).
            conv = None
            try:
                m = re.search(r'"conversationId"\s*:\s*"([^\"]+)"', payload)
                if m:
                    conv = m.group(1)
            except Exception:
                conv = None

            # Fallback to parsed JSON value if regex didn't find a string
            if conv is None:
                conv = frame.get('conversationId')

            print(f"Received on {msg.topic}: type={mtype} conversationId={conv}")
            if mtype and mtype.lower() == 'callforproposal' and conv:
                # store the exact conversation id string as received (no modification)
                received = {'conversationId': conv, 'sender': frame.get('sender', {}).get('id')}
        except Exception as e:
            print(f"Failed to parse incoming message: {e}")

    client.on_connect = on_connect
    client.on_message = on_message

    client.connect(args.broker, args.port, keepalive=60)
    client.loop_start()

    print(f"Waiting {args.initial_delay}s for agent to initialize...")
    time.sleep(args.initial_delay)

    waited = 0
    while waited < args.timeout and received is None:
        time.sleep(0.2)
        waited += 0.2

    if received is None:
        print(f"No callForProposal received within {args.timeout}s")
        client.loop_stop()
        client.disconnect()
        sys.exit(1)

    conv = received['conversationId']
    print(f"Found conversationId {conv}, sending {args.respond_type} to {resp_topic}")

    # Ensure we return the exact same conversationId string
    payload = make_response(conv, args.respond_type, args.sender, receiver_id=args.receiver)
    client.publish(resp_topic, payload, qos=1)
    print("Published response payload:")
    print(payload)

    time.sleep(0.5)
    client.loop_stop()
    client.disconnect()
    sys.exit(0)


if __name__ == '__main__':
    main()
