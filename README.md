# NinjaTrader Claude MCP

**Connect Claude AI to NinjaTrader 8 and let it read the market and execute trades.**

> Created by [Felipe Kuhn](https://github.com/felipekuhn87)

---

## 🇺🇸 English

### What is this?

This project connects **Claude AI** to **NinjaTrader 8** using the MCP (Model Context Protocol).

Claude gains access to 17 tools to read market data and execute orders in real time — autonomously, based on a prompt you write describing your own trading rules.

### Architecture

```text
Claude Code
    │
    │  stdio (MCP protocol)
    ▼
ninjatrader-mcp/   ← Node.js bridge (this repo)
    │
    ├── ws://localhost:8000/ws  →  NinjaMCPServer.cs  (NinjaTrader indicator — market data)
    └── ws://localhost:8002     →  NinjaMCPExecute.cs (NinjaTrader strategy — order execution)
```

There are **3 components** that must all be running:

| Component | What it is | Where it runs |
|---|---|---|
| `NinjaMCPServer.cs` | NinjaTrader **indicator** — streams market data | NinjaTrader chart (port 8000) |
| `NinjaMCPExecute.cs` | NinjaTrader **strategy** — executes orders | NinjaTrader chart (port 8002) |
| `ninjatrader-mcp/` | Node.js **bridge** — connects Claude to NinjaTrader | Your terminal |

### Prerequisites

- **NinjaTrader 8** — [ninjatrader.com](https://ninjatrader.com) (free for simulation)
- **Node.js 18+** — [nodejs.org](https://nodejs.org)
- **Claude Code** CLI — [install guide](https://docs.anthropic.com/en/docs/claude-code)
- A **Claude subscription** (Max plan) or [Anthropic API key](https://console.anthropic.com/)

### Installation — Step by step

#### Step 1 — Add the NinjaScript files to NinjaTrader

You need to add two files: one indicator and one strategy.

#### NinjaMCPServer (indicator)

1. Open NinjaTrader 8
2. Go to **Tools → Edit NinjaScript → Indicator**
3. Click **New** and name it `NinjaMCPServer`
4. Replace all the generated code with the contents of `NinjaMCPServer.cs`
5. Press **F5** to compile — no errors should appear

#### NinjaMCPExecute (strategy)

1. Go to **Tools → Edit NinjaScript → Strategy**
2. Click **New** and name it `NinjaMCPExecute`
3. Replace all the generated code with the contents of `NinjaMCPExecute.cs`
4. Press **F5** to compile

#### Step 2 — Apply them to a chart

1. Open a chart for the instrument you want to trade (e.g. MNQ 06-25)
2. Add the **indicator**: right-click chart → **Indicators** → find `NinjaMCPServer` → add it
3. Add the **strategy**: right-click chart → **Strategies** → find `NinjaMCPExecute` → add it
4. Set the account to **Simulated** while testing
5. Both should now be active — NinjaTrader is listening on ports 8000 and 8002

#### Step 3 — Install the Node.js bridge

```bash
cd ninjatrader-mcp
npm install
```

On Windows you can also run:

```cmd
scripts\install-mcp.bat
```

Test the connection to NinjaTrader:

```bash
npm run test-connection
```

#### Step 4 — Connect Claude Code to the MCP server

Add the bridge to your Claude Code MCP configuration.

##### Option A — via terminal (easiest)

```bash
claude mcp add ninjatrader -- node /full/path/to/ninjatrader-mcp/src/server.js
```

##### Option B — via config file

Edit your Claude Code settings file (`claude_desktop_config.json` or `.mcp.json`):
```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["/full/path/to/ninjatrader-mcp/src/server.js"]
    }
  }
}
```

Replace `/full/path/to/` with the actual path where you cloned this repo.

#### Step 5 — Write your prompt and start

Open `example_prompt.md` to understand the structure and available tools.
Then write your own prompt, start Claude Code, and let it connect.

### Available tools

| Tool | Description |
|---|---|
| `nt_health_check` | Check if both NinjaTrader connections are alive |
| `nt_get_status` | Full state: position, entry, stop, target, current price |
| `nt_get_position` | Current position (flat / long / short) |
| `nt_get_quote` | Live bid/ask/last price and volume |
| `nt_get_bars` | OHLCV bars from the chart (use `summary=true` for compact stats) |
| `nt_get_current_bar` | The bar currently forming (not yet closed) |
| `nt_get_indicators` | List all indicators loaded on the chart |
| `nt_get_indicator_value` | Read a specific indicator value by name |
| `nt_get_time_sales` | Recent time & sales tape |
| `nt_enter_long` | Buy — qty, stop price, target price |
| `nt_enter_short` | Sell — qty, stop price, target price |
| `nt_set_stoploss` | Move the stop loss |
| `nt_set_profit` | Move the take profit |
| `nt_breakeven` | Move stop to entry price |
| `nt_cancel_all_stops` | Cancel all stop orders |
| `nt_cancel_profit` | Cancel take profit |
| `nt_exit_all` | Flatten position immediately |

### Supported instruments

Any instrument you can trade in NinjaTrader works — the server reads from whatever chart it is applied to. Examples:

- Futures: MNQ, NQ, ES, MES, CL, MGC, GC, RTY, M2K
- Forex: EUR/USD, GBP/USD, and others available in NinjaTrader

### Important warnings

> **This software does not make trading decisions for you.**
> You are responsible for writing the trading logic in your prompt.
> Always test in simulation mode before using real money.
> Trading futures involves significant risk of loss.

---

## 🇧🇷 Português

### O que é isso?

Este projeto conecta o **Claude AI** ao **NinjaTrader 8** usando o protocolo MCP (Model Context Protocol).

O Claude ganha acesso a 17 ferramentas para ler dados de mercado e executar ordens em tempo real — de forma autônoma, com base em um prompt que você mesmo escreve descrevendo suas regras de operação.

### Arquitetura

```text
Claude Code
    │
    │  stdio (protocolo MCP)
    ▼
ninjatrader-mcp/   ← bridge Node.js (este repositório)
    │
    ├── ws://localhost:8000/ws  →  NinjaMCPServer.cs  (indicador NinjaTrader — dados de mercado)
    └── ws://localhost:8002     →  NinjaMCPExecute.cs (estratégia NinjaTrader — execução de ordens)
```

São **3 componentes** que precisam estar rodando ao mesmo tempo:

| Componente | O que é | Onde roda |
|---|---|---|
| `NinjaMCPServer.cs` | **Indicador** NinjaTrader — transmite dados de mercado | Gráfico NinjaTrader (porta 8000) |
| `NinjaMCPExecute.cs` | **Estratégia** NinjaTrader — executa ordens | Gráfico NinjaTrader (porta 8002) |
| `ninjatrader-mcp/` | **Bridge** Node.js — conecta Claude ao NinjaTrader | Seu terminal |

### Pré-requisitos

- **NinjaTrader 8** — [ninjatrader.com](https://ninjatrader.com) (gratuito para simulação)
- **Node.js 18+** — [nodejs.org](https://nodejs.org)
- **Claude Code** CLI — [guia de instalação](https://docs.anthropic.com/en/docs/claude-code)
- **Assinatura Claude** (plano Max) ou [chave de API Anthropic](https://console.anthropic.com/)

### Instalação — Passo a passo

#### Passo 1 — Adicionar os arquivos NinjaScript ao NinjaTrader

Você precisa adicionar dois arquivos: um indicador e uma estratégia.

#### NinjaMCPServer (indicador)

1. Abra o NinjaTrader 8
2. Vá em **Tools → Edit NinjaScript → Indicator**
3. Clique em **New** e dê o nome `NinjaMCPServer`
4. Substitua todo o código gerado pelo conteúdo do arquivo `NinjaMCPServer.cs`
5. Pressione **F5** para compilar — não deve aparecer nenhum erro

#### NinjaMCPExecute (estratégia)

1. Vá em **Tools → Edit NinjaScript → Strategy**
2. Clique em **New** e dê o nome `NinjaMCPExecute`
3. Substitua todo o código gerado pelo conteúdo do arquivo `NinjaMCPExecute.cs`
4. Pressione **F5** para compilar

#### Passo 2 — Aplicar em um gráfico

1. Abra um gráfico do instrumento que deseja operar (ex: MNQ 06-25)
2. Adicione o **indicador**: clique direito no gráfico → **Indicators** → encontre `NinjaMCPServer` → adicione
3. Adicione a **estratégia**: clique direito → **Strategies** → encontre `NinjaMCPExecute` → adicione
4. Defina a conta como **Simulated** durante os testes
5. Os dois devem estar ativos — o NinjaTrader estará ouvindo nas portas 8000 e 8002

#### Passo 3 — Instalar o bridge Node.js

```bash
cd ninjatrader-mcp
npm install
```

No Windows você também pode executar:

```cmd
scripts\install-mcp.bat
```

Teste a conexão com o NinjaTrader:

```bash
npm run test-connection
```

#### Passo 4 — Conectar o Claude Code ao servidor MCP

Adicione o bridge à configuração de MCP do Claude Code.

##### Opção A — via terminal (mais fácil)

```bash
claude mcp add ninjatrader -- node /caminho/completo/para/ninjatrader-mcp/src/server.js
```

##### Opção B — via arquivo de configuração

Edite o arquivo de configuração do Claude Code (`claude_desktop_config.json` ou `.mcp.json`):
```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["/caminho/completo/para/ninjatrader-mcp/src/server.js"]
    }
  }
}
```

Substitua `/caminho/completo/para/` pelo caminho real onde você clonou este repositório.

#### Passo 5 — Escreva seu prompt e comece

Abra o arquivo `example_prompt.md` para entender a estrutura e as ferramentas disponíveis.
Depois escreva o seu próprio prompt, inicie o Claude Code e deixe ele conectar.

### Ferramentas disponíveis

| Ferramenta | Descrição |
|---|---|
| `nt_health_check` | Verifica se as duas conexões com o NinjaTrader estão ativas |
| `nt_get_status` | Estado completo: posição, entrada, stop, alvo, preço atual |
| `nt_get_position` | Posição atual (flat / comprado / vendido) |
| `nt_get_quote` | Preço bid/ask/last e volume em tempo real |
| `nt_get_bars` | Barras OHLCV do gráfico (`summary=true` para versão compacta) |
| `nt_get_current_bar` | A barra em formação (ainda não fechada) |
| `nt_get_indicators` | Lista os indicadores carregados no gráfico |
| `nt_get_indicator_value` | Lê o valor de um indicador específico |
| `nt_get_time_sales` | Time & sales recentes |
| `nt_enter_long` | Comprar — contratos, preço de stop, preço alvo |
| `nt_enter_short` | Vender — contratos, preço de stop, preço alvo |
| `nt_set_stoploss` | Mover o stop loss |
| `nt_set_profit` | Mover o take profit |
| `nt_breakeven` | Mover stop para o preço de entrada |
| `nt_cancel_all_stops` | Cancelar todas as ordens de stop |
| `nt_cancel_profit` | Cancelar o alvo de lucro |
| `nt_exit_all` | Fechar posição imediatamente |

### Instrumentos suportados

Qualquer instrumento que você possa operar no NinjaTrader funciona — o servidor lê do gráfico em que está aplicado. Exemplos:

- Futuros: MNQ, NQ, ES, MES, CL, MGC, GC, RTY, M2K
- Forex: EUR/USD, GBP/USD e outros disponíveis no NinjaTrader

### Avisos importantes

> **Este software não toma decisões de trading por você.**
> Você é responsável por escrever a lógica de operação no seu prompt.
> Sempre teste em modo simulado antes de usar dinheiro real.
> Operar futuros envolve risco significativo de perda.

---

## License

MIT — free to use, modify, and distribute.

---

*Built with ❤️ by Felipe Kuhn — feel free to contribute, open issues, or share your results.*
