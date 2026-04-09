# Como Ativar — Setup Passo a Passo

Guia completo para deixar o `ninjatrader-mcp` operando do zero.

---

## Pré-requisitos

- **NinjaTrader 8** instalado, conectado a um broker (Tradovate / Apex / Rithmic)
- **Node.js 18+** (`node --version`)
- **Claude Code** instalado
- Conta com **permissão de strategies automatizadas** habilitada pelo broker
  > Algumas contas Apex PA bloqueiam strategies. Evaluation accounts geralmente permitem.

---

## 1. Compilar os arquivos C# no NinjaScript Editor

Os dois `.cs` da raiz do repo precisam ser compilados dentro do NinjaTrader.

### 1.1 Indicator (`NinjaMCPServer.cs`)

```
NinjaTrader 8 → New → NinjaScript Editor
  → Indicators → New Indicator → Wizard
    → Name: NinjaMCPServer
    → OK
  → Cole o CONTEÚDO COMPLETO de NinjaMCPServer.cs (sobrescrever o template)
  → F5 (Compile)
```

Se a compilação reclamar de **classe duplicada** (`NinjaSocketServer` antigo no diretório do usuário), feche o NinjaTrader e delete:

```
%USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Indicators\NinjaSocketServer.cs
```

Reabra o NT e compile novamente.

### 1.2 Strategy (`NinjaMCPExecute.cs`)

```
NinjaScript Editor
  → Strategies → New Strategy → Wizard
    → Name: NinjaMCPExecute
    → OK
  → Cole o CONTEÚDO COMPLETO de NinjaMCPExecute.cs
  → F5 (Compile)
```

Mesma observação para `NinjaSocketStrategyFk.cs` antigo (`Strategies/`).

---

## 2. Ativar Indicator no Chart

```
Chart de MNQ (ou seu instrumento) → Right click → Indicators
  → Adicionar: NinjaMCPServer
    Server Address:   ws://localhost:8000/ws
    Max Stored Bars:  1000
  → Apply → OK
```

**Verifique no NT Output Window** (`New → NinjaScript Output`):

```
[NinjaMCPServer] WebSocket ouvindo em http://localhost:8000/
[NinjaMCPServer] Histórico carregado (XXX barras), capturando buffer...
[NinjaMCPServer] Buffer pronto com XXX barras.
```

---

## 3. Ativar Strategy no Chart

```
Chart → Strategies (botão na barra)
  → New
    Strategy:           NinjaMCPExecute
    Server Address:     ws://localhost:8002
    Default Contracts:  2 (ou seu padrão)
    Use Fixed Contracts: false
    Trade Log Path:     C:\Users\<você>\Documents\NinjaTrader 8\bin\Custom\Strategies\trade_log.csv
    Account:            Sim101 (ou conta real depois)
  → Enable: ✓
```

**Output esperado:**
```
[NinjaMCPExecute] WS listening at 8002
```

---

## 4. Instalar o MCP server (Node.js)

```bash
cd C:/Users/felip/OneDrive/Documentos/GitHub/mpc_ninja_trader_kuhn/ninjatrader-mcp
npm install
```

---

## 5. Testar conexão (sem Claude)

Antes de adicionar ao Claude Code, teste standalone:

```bash
npm run test-connection
```

**Saída esperada:**
```
[DataClient] Connected to ws://localhost:8000/ws
[OrdersClient] Connected to ws://localhost:8002
✅ Both connections OK
```

Se falhar → ver `TROUBLESHOOTING.md`.

---

## 6. Registrar no Claude Code

Adicionar ao `.mcp.json` do projeto (ou ao global em `~/.claude.json`):

```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["C:/Users/felip/OneDrive/Documentos/GitHub/mpc_ninja_trader_kuhn/ninjatrader-mcp/src/server.js"],
      "env": {
        "NT_DATA_WS": "ws://localhost:8000/ws",
        "NT_ORDERS_WS": "ws://localhost:8002"
      }
    }
  }
}
```

**Reinicie o Claude Code.** As 19 tools `nt_*` ficarão disponíveis.

---

## 7. Validação rápida no Claude

Peça ao Claude:

```
nt_health_check
nt_get_quote
nt_get_bars(count=10, summary=true)
nt_get_indicators
nt_get_status
```

Tudo deve retornar dados do chart ativo. Se `nt_get_indicators` retornar lista vazia, garanta que tem indicators no chart além do `NinjaMCPServer`.

---

## 8. Próximos passos

- Copie `rules.example.json` → `rules.json` e ajuste os valores da sua conta (max_daily_loss, contracts, session hours).
- Rode primeiro em **conta SIM/DEMO**. Veja `EXAMPLES.md` para fluxos de trading autônomo.
- Só vá pra conta real depois de validar trade log CSV completo (entries, stops, breakeven, exits).

---

## Recompilação

Toda mudança em `.cs` → F5 no NinjaScript Editor → Indicator/Strategy precisa ser **removido e re-adicionado** ao chart.

Mudanças em `.js` → reiniciar Claude Code (o MCP server roda dentro do processo do Claude).
