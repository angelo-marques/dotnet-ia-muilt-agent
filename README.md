# 🤖 dotnet-ai-multi-agent

Sistema multi-agente em C# / .NET 9 para geração automatizada de sistemas de software com garantia de qualidade. Clean Architecture com validação em 5 gates (compilação, null safety, code review, cobertura de testes e arquitetura).


---

## 🏗️ Arquitetura

```
User Input / Arquivo Grande
        │
        ▼
ArchitectAgent       → Define Clean Architecture, projetos, decisões
        │
        ▼
DecomposerAgent      → Roslyn analisa deps reais, ordena em camadas topológicas
        │
        ▼
SmartRetryOrchestrator
    ├── Layer 0: [WI-001, WI-002] → paralelo → CodeGen → Roslyn → Review → Test
    ├── Layer 1: [WI-003, WI-004] → paralelo → CodeGen → Roslyn → Review → Test
    └── Layer N: ...
        │
        ▼
ValidationPipeline (5 gates obrigatórios)
    ① CompilationGate    (Roslyn — zero erros)
    ② NullSafetyGate     (Roslyn — nullable)
    ③ CodeReviewGate     (LLM — score ≥ 8.0)
    ④ TestCoverageGate   (LLM — cenários cobertos)
    ⑤ ArchitecturalGate  (LLM — Clean Arch respeitada)
        │
        ▼
📺 Output: SignalR (browser) + Spectre.Console (terminal) + Serilog (logs)
```

---

## 🚀 Início Rápido

### Pré-requisitos

- .NET 9 SDK
- Ollama com modelo llama3.2 (ou chave OpenAI)
- AnythingLLM (opcional, para RAG)

### 1. Clone e configure

```bash
git clone <repo>
cd dotnet-ai-multi-agent
```

### 2. Configure o LLM

**Opção A — Ollama (local, grátis):**
```bash
ollama pull llama3.2
# ou modelos melhores:
ollama pull qwen2.5-coder:7b
ollama pull deepseek-coder-v2
```

**Opção B — OpenAI:**
```json
// src/MultiAgent.Host/appsettings.json
{
  "LLM": {
    "Provider": "openai",
    "OpenAiKey": "sk-...",
    "OpenAiModel": "gpt-4o-mini"
  }
}
```

### 3. Configure AnythingLLM (opcional mas recomendado)

```bash
# Docker
docker run -d -p 3001:3001 mintplexlabs/anythingllm

# Acesse http://localhost:3001
# Crie workspaces: dotnet-best-practices, design-patterns, testing-patterns
# Suba documentação .NET, exemplos de Clean Architecture, etc.
```

```json
// appsettings.json
{
  "AnythingLLM": {
    "BaseUrl": "http://localhost:3001",
    "ApiKey": "seu-api-key-do-anythingllm"
  }
}
```

### 4. Execute

```bash
cd src/MultiAgent.Host
dotnet run

# Acesse o dashboard: http://localhost:5000
```

---

## 📡 API

### Gerar sistema a partir de texto
```http
POST /api/generate
Content-Type: application/json

{
  "input": "Crie um sistema de gerenciamento de pedidos com pagamento e notificações"
}
```

### Gerar a partir de arquivo grande
```http
POST /api/generate/file
Content-Type: multipart/form-data

file: <arquivo .cs, .md, .txt, spec>
```

### Status dos agentes
```http
GET /api/status
```

---

## 🔄 Garantia de Convergência

O `SmartRetryOrchestrator` usa escalada inteligente em 5 níveis:

| Tentativas | Estratégia | O que faz |
|---|---|---|
| 1-2 | Normal | Corrige os erros identificados |
| 3-4 | Rewrite | Reescreve do zero com outra abordagem |
| 5-6 | Simplify | Remove abstrações, implementa só o essencial |
| 7-8 | RagAssisted | Busca exemplos no AnythingLLM |
| 9-10 | ArchitectHelp | Redesenha a estrutura do componente |

**Detecção de loop:** Se o mesmo erro aparece 3x seguidas → forçar redesenho imediato.

---

## 🔬 DecomposerAgent

Para arquivos grandes, o `DecomposerAgent`:

1. **Roslyn analisa** dependências reais do código (se for C#)
2. **Detecta ciclos** com DFS antes de qualquer geração
3. **Ordena topologicamente** — Layer 0 nunca depende de Layer 1
4. **Grupos paralelos** — WorkItems sem dependência entre si rodam simultaneamente
5. **Fallback** — se JSON falhar, usa decomposição mínima

---

## 📦 Pacotes

| Pacote | Uso |
|---|---|
| `Microsoft.CodeAnalysis.CSharp` | Compilação e análise em memória |
| `Microsoft.Extensions.AI` | Abstração universal de LLM |
| `Microsoft.Extensions.AI.Ollama` | Provider Ollama |
| `Microsoft.Extensions.AI.OpenAI` | Provider OpenAI |
| `System.Threading.Channels` | Comunicação entre agentes |
| `Microsoft.AspNetCore.SignalR` | Output em tempo real no browser |
| `Spectre.Console` | Output rico no terminal |
| `Serilog` | Logs estruturados |
| `xunit` + `NSubstitute` | Testes |

---

## 🗂️ Estrutura do Projeto

```
dotnet-ai-multi-agent/
├── src/
│   ├── MultiAgent.Core/
│   │   ├── Agents/
│   │   │   ├── BaseAgent.cs
│   │   │   ├── ArchitectAgent.cs
│   │   │   ├── DecomposerAgent.cs
│   │   │   ├── CodeGeneratorAgent.cs
│   │   │   ├── ValidatorAndReviewerAgents.cs
│   │   │   └── TestAgent.cs
│   │   ├── Bus/
│   │   │   ├── AgentMessage.cs
│   │   │   └── AgentMessageBus.cs
│   │   ├── Orchestration/
│   │   │   ├── SmartRetryOrchestrator.cs
│   │   │   └── MainOrchestrator.cs
│   │   ├── Validation/
│   │   │   ├── RoslynCodeValidator.cs
│   │   │   ├── ValidationPipeline.cs
│   │   │   └── Gates/
│   │   │       └── ValidationGates.cs
│   │   ├── RAG/
│   │   │   └── AnythingLLMClient.cs
│   │   └── Models/
│   │       └── Models.cs
│   └── MultiAgent.Host/
│       ├── Hubs/AgentHub.cs
│       ├── Program.cs
│       ├── appsettings.json
│       └── wwwroot/index.html
└── tests/
    └── MultiAgent.Tests/
        └── ValidationTests.cs
```

---

## 💡 Dicas

- **Melhor modelo local:** `qwen2.5-coder:7b` ou `deepseek-coder-v2` para geração de código
- **RAG bem alimentado** faz diferença enorme — suba docs de Clean Arch, SOLID, padrões .NET
- **Logs detalhados** em `logs/agent-*.log` para debug
- Dashboard disponível em `http://localhost:5000` para ver agentes em tempo real
