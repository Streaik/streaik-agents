# streaik-agents

A .NET agent and workflow runtime built on [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

Define agents and multi-agent workflows in portable YAML or client-side agent JSON, connect models, tools, [MCP](https://modelcontextprotocol.io/), [Skills](https://agentskills.io) and [Plugins](https://agent-plugins.org) and execute them through a consistent runtime exposed through familiar OpenAI, Anthropic, and AI SDK APIs.

The runtime supports the standard [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) orchestration patterns:

- **Sequential** - agents execute one after another
- **Concurrent** - agents execute in parallel
- **Handoff** - control moves between specialized agents
- **Group Chat** - agents collaborate in a shared conversation
- **Magentic** - a manager agent dynamically coordinates the team
- **Decisions** - one agent is selected and executed based on the input

**Documentation:** https://agents.streaik.net/scalar  
**API:** https://agents.streaik.net

## Quick start

```bash
curl "https://agents.streaik.net/v1/responses" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "model": "OpenAIAgent",
    "input": "Say hi"
  }'
```

```bash
curl "https://agents.streaik.net/v1/responses" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
   "agent": {
      "name": "GreetingAgent",
      "description": "Greets the user",
      "instructions": "Reply with a brief greeting.",
      "model": { "id": "openai/gpt-6-luna" }
    }, 
    "input": "Say hi"
  }'
```

```bash
curl "https://agents.streaik.net/v1/responses" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "models": ["GoogleSearchAgent", "OpenAIAgent"],
    "workflow_type": "sequential",
    "input": "Say hi"
  }'
```

```bash
curl "https://agents.streaik.net/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "agent": {
      "name": "GreetingAgent",
      "description": "Greets the user",
      "instructions": "Reply with a brief greeting.",
      "model": { "id": "openai/gpt-6-luna" }
    },
    "messages": [{"role":"user","content":"Say hi"}]
  }'
```

```bash
curl "https://agents.streaik.net/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "models": ["GoogleSearchAgent", "OpenAIAgent"],
    "workflow_type": "sequential",
    "messages": [{"role":"user","content":"Say hi"}]
  }'
```

```bash
curl "https://agents.streaik.net/v1/responses" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "agent": {
      "name": "InventoryAgent",
      "description": "Manages inventory",
      "instructions": "Help the user find inventory information.",
      "model": { "id": "openai/gpt-6-luna" },
      "tools": [
        {
          "type": "skills",
          "skills": [
            {
              "type": "skill_reference",
              "skill_id": "openai/skill_inventory"
            }
          ]
        },
        {
          "type": "mcp",
          "execution": "server",
          "server_label": "inventory",
          "server_url": "https://inventory.example.com/mcp"
        },
        {
          "type": "resource_search",
          "execution": "server"
        }
      ]
    },
    "input": "Which products are currently in stock?"
  }'
```

See the [API documentation](https://agents.streaik.net/scalar) for endpoints, authentication, request schemas and examples.