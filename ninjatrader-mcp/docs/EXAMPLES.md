# Examples — Fluxos Práticos de Uso

Casos de uso reais para o `ninjatrader-mcp` invocados pelo Claude Code.

---

## 1. Health Check inicial

```
Claude, verifique se o NinjaTrader está conectado.
```

→ Claude chama `nt_health_check`. Resposta esperada:
```json
{"success": true, "connected": true, "response": "PONG_HEALTH_CHECK"}
```

---

## 2. Snapshot de mercado

```
Claude, me dê um snapshot completo do MNQ agora.
```

Sequência típica:
```
nt_get_quote                          → bid/ask/last
nt_get_bars(count=20, summary=true)   → contexto recente
nt_get_indicators                     → todos os indicators do chart
nt_get_time_sales(count=20)           → últimas execuções
```

---

## 3. Análise pré-entrada com history de indicators

```
Claude, tem cruzamento de MACD nas últimas 5 barras?
```

→ Claude chama:
```js
nt_get_indicator_value({name: "MACD", bars: 5})
```

Resposta:
```json
{
  "name": "MACD",
  "plots": {
    "Macd": [12.45, 11.80, 10.50, 9.00, 8.10],
    "Avg":  [10.20, 10.10, 10.00, 9.80, 9.50],
    "Diff": [2.25, 1.70, 0.50, -0.80, -1.40]
  }
}
```

Claude analisa: `Macd` cruzou `Avg` pra cima entre índice 2 e 1 (10.50 > 10.00 → 11.80 > 10.10). Sinal bullish.

---

## 4. Detectar divergência RSI

```
Claude, tem divergência bullish do RSI nas últimas 20 barras?
```

→ Sequência:
```js
nt_get_bars({count: 20, summary: false})       // preços
nt_get_indicator_value({name: "RSI", bars: 20}) // RSI
```

Claude compara: preço fez Lower Low mas RSI fez Higher Low → divergência bullish.

---

## 5. Entrada Long com SL/TP atômico

```
Claude, entre LONG MNQ 2 contratos, stop em 24130, alvo em 24165.
```

→ Claude chama:
```js
nt_enter_long({qty: 2, sl: 24130, tp: 24165})
```

A strategy aplica `SetStopLoss` + `SetProfitTarget` ANTES do `EnterLong`, garantindo OCO atômico.

Verificação:
```js
nt_get_status   // confirma posição, stop e profit
```

---

## 6. Move-to-Breakeven

```
Claude, mova o stop pra breakeven com 2 pts de buffer.
```

→ `nt_breakeven` (sem argumentos = entry exato) ou `nt_breakeven({offset: 2})` (entry + 2 pts).

A strategy valida que o stop fica do lado correto antes de aplicar.

---

## 7. Trailing stop por estrutura

Loop manual:
```
Claude, monitore MNQ e mova o stop pro último HL a cada novo swing high.
```

→ Claude faz:
```js
// loop a cada 30s
nt_get_bars({count: 30, summary: false})
// detecta novo swing high
const newSL = identifyLastHigherLow(bars);
nt_set_stoploss({price: newSL})
```

A strategy rejeita stop wrong-side automaticamente — Claude não precisa validar.

---

## 8. Exit por sinal contrário

```
Claude, saia tudo se o MACD cruzar pra baixo.
```

→ Claude monitora:
```js
nt_get_indicator_value({name: "MACD", bars: 3})
```

Se detecta cruzamento bearish:
```js
nt_exit_all
```

---

## 9. Loop de operação autônoma (5min)

Comando do usuário:
```
/loop 5m scan e opera MNQ via NinjaTrader autonomamente
```

A cada 5 min Claude executa:

```js
// 1. SCAN
nt_health_check
nt_get_status
nt_get_quote
nt_get_bars({count: 50, summary: true})
nt_get_indicators({bars: 5})    // history pra detectar cruzamentos
nt_get_time_sales({count: 30})

// 2. ANÁLISE (interna)
// Aplica framework SMC: BOS, CHoCH, OBs, FVGs, Liquidity
// Calcula confluência. Skip se < 4 fatores.

// 3. DECISÃO
// Se IN_POSITION: HOLD / ADJUST / EXIT
// Se FLAT: ENTER ou SKIP

// 4. EXECUÇÃO (se aplicável)
nt_enter_long({qty: 2, sl: ..., tp: ...})
// OU
nt_set_stoploss({price: ...})
// OU
nt_exit_all

// 5. OUTPUT 1-3 linhas
"FLAT - sem setup, confluência 2/4"
"LONG @ 24140, SL 24128, TP 24165"
"Stop movido para 24135 (BE)"
```

---

## 10. Flatten obrigatório no fechamento

Cron interno do Claude:
```
Às 15:55 ET, force flat tudo.
```

→
```js
nt_get_status
// Se in_position:
nt_exit_all
nt_get_status   // confirma flat
```

A strategy também tem `IsExitOnSessionCloseStrategy = true` como segurança redundante.

---

## 11. Verificar daily P&L antes de nova entrada

```
Claude, antes de qualquer entrada hoje, leia trade_log.csv e me diga o P&L acumulado.
```

→ Claude usa o `Read` tool nativo (não MCP) para abrir o `trade_log.csv` que a strategy escreve, soma a coluna `pnl`, decide se ainda pode operar baseado no `circuit_breaker` do `rules.json`.

---

## Padrão recomendado para LLM

| Situação | Tools a chamar |
|---|---|
| Início de sessão | `nt_health_check` → `nt_get_status` |
| Decidir entrada | `nt_get_bars(50)` + `nt_get_indicators(bars=5)` |
| Monitorar posição | `nt_get_status` + `nt_get_quote` (cheap) |
| Trailing | `nt_get_bars(20)` + `nt_set_stoploss` |
| Exit forçado | `nt_exit_all` + `nt_get_status` (confirmar) |
| Fim de sessão | `nt_exit_all` (mesmo se flat — idempotente) |

**Princípio:** chamadas com `bars=1` são baratas (use livremente). Chamadas com `bars>5` são caras em tokens — use só na fase de decisão.
