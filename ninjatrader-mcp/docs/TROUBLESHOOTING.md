# Troubleshooting

Diagnóstico para os problemas mais comuns. Sempre comece com `nt_health_check`.

---

## 🔴 `npm run test-connection` falha — `ECONNREFUSED`

**Causa:** Indicator/Strategy não está rodando, ou porta errada.

**Checar:**
1. Indicator `NinjaMCPServer` aplicado a algum chart? (Right-click chart → Indicators → ver se aparece na lista ativa)
2. Strategy `NinjaMCPExecute` enabled? (Strategies tab → check Enabled)
3. Output Window mostra `WebSocket ouvindo em http://localhost:8000/`?
4. Portas 8000/8002 livres? `netstat -ano | findstr "8000 8002"` deve mostrar `LISTENING`.

---

## 🔴 Compilação C# falha: "Class already exists"

**Causa:** Arquivo antigo (`NinjaSocketServer.cs` / `NinjaSocketStrategyFk.cs`) ainda no diretório de usuário do NT.

**Fix:**
```
1. Feche o NinjaTrader 8 completamente (incluindo tray icon)
2. Delete:
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Indicators\NinjaSocketServer.cs
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\Strategies\NinjaSocketStrategyFk.cs
3. Delete também os .dll cacheados:
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\NinjaTrader.Custom.dll
4. Reabra o NT — ele recompila tudo automaticamente
```

---

## 🟡 `nt_get_indicators` retorna `[]` vazio

**Possíveis causas:**

| Causa | Fix |
|---|---|
| Chart só tem o `NinjaMCPServer` (que é skipped) | Adicione SMA/EMA/RSI/etc ao chart |
| Indicator foi adicionado mas chart não foi salvo | `Ctrl+S` no chart |
| `ChartControl` ainda nulo no startup | Aguarde uns segundos após adicionar indicator |

---

## 🟡 `nt_get_quote` retorna tudo zero

**Causa:** Mercado fechado, ou nenhum tick chegou ainda.

**Fix:** Aguarde o pregão abrir, ou olhe o chart real-time para confirmar feed ativo.

---

## 🔴 `nt_enter_long` retorna `ERROR:Unknown_Command`

**Causa:** Strategy não tá processando o comando — pode ter parado por erro interno ou não tá em estado `Realtime`.

**Checar:**
1. Strategy mostra `Enabled` no painel Strategies?
2. NT Output mostra alguma exception em `[NinjaMCPExecute]`?
3. Strategy precisa estar em `State.Realtime` — só fica nesse estado depois que o histórico carrega. Aguarde alguns segundos após enable.

---

## 🔴 Posição aberta mas `nt_get_status` retorna `in_position=false`

**Causa raiz comum:** Você fechou e reabriu o NinjaTrader com posição aberta — a strategy perdeu o `lastEntrySignal`.

**Fix:** A função `EnsureSignalName()` no `NinjaMCPExecute.cs` recupera isso na próxima operação. Mas se quiser status correto imediato, force uma chamada:
```
nt_set_stoploss(price=<algum valor válido>)
```
Isso rehydrata o signal name. Aí `nt_get_status` reflete o estado real.

---

## 🟡 Stop não move com `nt_set_stoploss`

**Possíveis causas:**

| Erro retornado | Significa |
|---|---|
| `ERROR:StopLoss_Wrong_Side` | Stop do lado errado (acima do preço em LONG ou abaixo em SHORT). Validação intencional pra evitar fechar posição instantaneamente. |
| `ERROR:No_Active_Position` | Sem posição. Verifique `nt_get_status`. |
| `ERROR:Invalid_StopLoss_Format` | Preço não-numérico ou ≤ 0. |

---

## 🟡 Latência alta (>500ms) entre comando e fill

**Não é o MCP — é o broker.**

Latência típica:
- Claude → MCP → NT: ~10-20ms
- NT → broker (Tradovate): 30-100ms
- Broker → exchange: 20-80ms

Se passa de 500ms, problema é rede/broker, não código.

---

## 🔴 Claude Code não vê as tools `nt_*`

**Checar:**
1. `.mcp.json` no path correto? (raiz do projeto OU `~/.claude.json` global)
2. Path no `args` aponta pro `server.js` correto?
3. Reiniciou o Claude Code completamente após salvar `.mcp.json`?
4. Veja `claude --debug` ou logs do Claude pra ver se o MCP server crashou no startup.

**Teste manual:**
```bash
node "C:/Users/felip/OneDrive/Documentos/GitHub/mpc_ninja_trader_kuhn/ninjatrader-mcp/src/server.js"
```
Deve hangar (rodando stdio). Ctrl+C pra sair. Se der erro de import → `npm install` faltando.

---

## 🔴 `WebSocket connection refused` no startup do MCP

**Não é fatal.** O `data-client.js` e `orders-client.js` foram desenhados pra **reconectar automaticamente**. O MCP sobe mesmo sem NT rodando.

**Mensagem esperada no stderr:**
```
[DataClient] Connection failed: ECONNREFUSED. Retrying in 2s...
```

Quando você ativar o indicator/strategy no NT, ele auto-conecta na próxima tentativa.

---

## 🟡 `nt_get_indicators({bars: 10})` retorna `value` em vez de `values`

**Causa:** C# antigo (sem o feature multi-bar) ainda compilado. Recompile o `NinjaMCPServer.cs` (F5) e re-aplique ao chart.

---

## Ainda travado?

1. Capture o output do `NT Output Window` filtrando por `[NinjaMCPServer]` e `[NinjaMCPExecute]`
2. Capture stderr do MCP server (Claude Code mostra em logs)
3. Rode `nt_health_check` e `nt_get_status` e copie a resposta
