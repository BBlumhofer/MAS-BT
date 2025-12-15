# Startup and MQTT Integration

## Overview

This document explains how to start MAS-BT agents and configure MQTT integration for messaging. It includes recommended MQTT settings and common troubleshooting steps.

## Quick Start

- Use `dotnet run --project MAS-BT/MAS-BT.csproj -- <agent-config>` to start an agent.
- Example config keys: `config.MQTT.Broker`, `config.MQTT.Port`, `config.Namespace`, `config.Agent.AgentId`.

## MQTT Recommendations

- Use unique client IDs per agent instance.
- Use `QoS=1` for critical messages and set `RetainMessages` only where needed.
- Monitor and tune `KeepAliveInterval` and `ReconnectDelay` for unstable networks.

## Troubleshooting

- Verify broker connectivity and credentials.
- Inspect topic subscriptions listed at startup.
