# ninjatrader-mcp

MCP (Model Context Protocol) server that bridges Claude Code to NinjaTrader 8.
Exposes 17 tools for reading market data, indicators, Time & Sales, and
executing orders with atomic stop loss / take profit.

See `../NINJATRADER-MCP-SPEC.md` for the full technical specification.

## Architecture

```
Claude Code <-- stdio --> ninjatrader-mcp (Node.js)
                                |
                                +-- ws://localhost:8000/ws (NinjaMCPServer.cs indicator)
                                |       data: bars, quote, indicators, T&S
                                |
                                +-- ws://localhost:8002     (NinjaMCPExecute.cs strategy)
                                        orders: enter/exit, stop/profit mods, status
```

The MCP server starts even if NinjaTrader is offline. Both WebSocket clients
reconnect automatically every 2 seconds. Tool calls return
`{success:false,error:...}` while disconnected — they do NOT crash the server.

## Requirements

- Node.js 18+
- NinjaTrader 8 with the `NinjaMCPServer` indicator applied to a chart
  (port 8000) and the `NinjaMCPExecute` strategy enabled (port 8002).

## Installation

```bash
cd ninjatrader-mcp
npm install
```

Or run the helper batch script on Windows:

```cmd
scripts\install-mcp.bat
```

## Test the connection

With NinjaTrader running, chart open, indicator and strategy active:

```bash
npm run test-connection
```

Expected output:

```
[1/2] Testing DATA endpoint: ws://localhost:8000/ws
      OK -- response: PONG_HEALTH_CHECK
[2/2] Testing ORDERS endpoint: ws://localhost:8002
      OK -- response: STATUS:InPosition=false;...
Both connections OK
```

## Register in Claude Code

Add to your project's `.mcp.json`:

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

Restart Claude Code. The 17 `nt_*` tools will become available.

## Tools

### Data tools (port 8000 — NinjaMCPServer)

| Tool | Description |
|------|-------------|
| `nt_health_check` | Ping the data WebSocket |
| `nt_get_bars(count, summary)` | OHLCV bars, optionally as compact summary |
| `nt_get_current_bar` | Current in-progress bar |
| `nt_get_quote` | Best bid/ask/last/volume |
| `nt_get_indicators` | Discover all chart indicators and current plot values |
| `nt_get_indicator_value(name)` | Current values of a specific indicator |
| `nt_get_time_sales(count)` | Recent tick prints |

### Orders tools (port 8002 — NinjaMCPExecute)

| Tool | Description |
|------|-------------|
| `nt_enter_long(qty, sl?, tp?)` | Enter LONG with atomic SL/TP |
| `nt_enter_short(qty, sl?, tp?)` | Enter SHORT with atomic SL/TP |
| `nt_set_stoploss(price)` | Modify stop loss |
| `nt_set_profit(price)` | Modify profit target |
| `nt_breakeven(offset?)` | Move stop to breakeven |
| `nt_cancel_all_stops` | Cancel all stop orders |
| `nt_cancel_profit` | Cancel profit target |
| `nt_exit_all` | Flatten position |
| `nt_get_status` | Full strategy status |
| `nt_get_position` | Position-only subset of status |

## Environment variables

| Variable | Default |
|----------|---------|
| `NT_DATA_WS` | `ws://localhost:8000/ws` |
| `NT_ORDERS_WS` | `ws://localhost:8002` |

## Project layout

```
ninjatrader-mcp/
├── package.json
├── README.md
├── rules.example.json        # Strategy rules template (see spec section 7)
├── src/
│   ├── server.js             # MCP entry point (stdio)
│   ├── data-client.js        # WebSocket client :8000
│   ├── orders-client.js      # WebSocket client :8002
│   ├── tools/
│   │   ├── data-tools.js     # 7 data tools
│   │   ├── orders-tools.js   # 10 order tools
│   │   └── index.js
│   └── parsers/
│       ├── bars-parser.js
│       ├── dom-parser.js       # quote + time&sales
│       ├── indicators-parser.js
│       └── status-parser.js
└── scripts/
    ├── test-connection.js
    └── install-mcp.bat
```

## Specification

See `../NINJATRADER-MCP-SPEC.md` for the complete spec including NinjaScript
modifications, strategy rules format, autonomous loop workflow and roadmap.
