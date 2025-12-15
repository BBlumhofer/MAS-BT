# Capability Matching with Neo4j

## Overview

This document describes how capability matching is implemented using Neo4j as a backend graph database. It outlines the data model, example queries and common usage patterns for finding compatible agents or resources based on capabilities and semantic relationships.

## Contents

- Data model: nodes and relationships for capabilities, agents and resources
- Example Cypher queries for matching and ranking candidates
- Integration notes: indexing, performance, and synchronization with MAS-BT

## Usage

- Use Neo4j to store capability ontologies and semantic links
- Query the graph when routing CFPs (Call for Proposals) or searching for compatible modules
- Tune indexes and caching for production workloads

For examples and detailed queries, refer to the project examples and the I4.0-Sharp-Messaging integration docs.
