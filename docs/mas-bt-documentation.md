# MAS-BT Project Documentation

## Overview

This document summarizes the MAS-BT project, its goals and core components. It provides pointers to implementation details and extension points for agent logic, behavior tree serialization, and messaging.

## Contents

- Project goals and scope.
- Core components: BehaviorTree serialization, Node Registry, Messaging integration.
- How to add new nodes, register them and update the `specs.json` when needed.

## Useful paths

- `MAS-BT/BehaviorTree/Serialization/XmlTreeDeserializer.cs` — tree loading and interpolation.
- `MAS-BT/BehaviorTree/Serialization/NodeRegistry.cs` — node registration.
