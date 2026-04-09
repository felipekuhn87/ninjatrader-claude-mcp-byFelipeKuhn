#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.Kuhns
{
    /// <summary>
    /// NinjaMCPExecute — Strategy de execução de ordens via WebSocket (porta 8002) controlada
    /// pelo ninjatrader-mcp (Node.js) / Claude Code. Comandos suportados:
    /// EnterLongOrder / EnterShortOrder (com SL/TP atômico), STOPLOSS, PROFIT, BREAKEVEN,
    /// CancelAllStops, CANCEL_PROFIT, ExitOrders, GetStatus, PING_HEALTH_CHECK.
    /// </summary>
    public class NinjaMCPExecute : Strategy, IDisposable
    {
        [NinjaScriptProperty]
        [Display(Name = "Server Address", GroupName = "Parameters", Order = 0)]
        public string ServerAddress { get; set; } = "ws://localhost:8002";

        [NinjaScriptProperty]
        [Display(Name = "Default Contracts", GroupName = "Parameters", Order = 1)]
        public int DefaultContracts { get; set; } = 1;

        [NinjaScriptProperty]
        [Display(Name = "Use Fixed Contracts", Description = "True = always use Default Contracts, ignoring Claude's qty. False = use Claude's qty.", GroupName = "Parameters", Order = 2)]
        public bool UseFixedContracts { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Trade Log Path", GroupName = "Parameters", Order = 3)]
        public string TradeLogPath { get; set; } = @"C:\Users\Administrator\Documents\NinjaTrader 8\bin\Custom\Strategies\trade_log.csv";

        private HttpListener http;
        private CancellationTokenSource cts;
        private readonly List<WebSocket> clients = new();
        private readonly object cliLock = new();
        private readonly List<string> wsMsgs = new();
        private readonly object msgLock = new();

        private string lastEntrySignal = null;
        private int entryCounter = 0;  // Incremental counter to avoid OCO ID collisions
        private double entryPrice = 0;
        private int activeContracts = 0;
        private bool stopLossSet = false;
        private double lastStopPrice = 0;
        private bool profitTargetSet = false;
        private double lastProfitPrice = 0;

        // Real stop/profit prices confirmed by NT order system (vs cached lastStopPrice)
        private double confirmedStopPrice = 0;
        private double confirmedProfitPrice = 0;

        // Exit tracking — captura preco real de saida
        private double exitPrice = 0;
        private string exitSide = "";
        private DateTime entryTime = DateTime.MinValue;
        private string pendingExitReason = "";
        private bool positionCloseHandled = false;

        // Fallback: averagePrice capturado quando posicao ABRE (antes de ir pra Flat)
        private double lastPositionAvgPrice = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Calculate = Calculate.OnEachTick;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
            }
            else if (State == State.Realtime)
            {
                cts = new CancellationTokenSource();
                StartWs();
            }
            else if (State == State.Terminated)
            {
                cts?.Cancel();
                StopWs();
            }
        }

        // Pending SL/TP para aplicar quando fill chegar (ordem atomica)
        private double pendingSL = 0;
        private double pendingTP = 0;

        /// <summary>
        /// Parses contract count from command. Format: "EnterShortOrder" (uses default) or "EnterShortOrder:2"
        /// Also supports atomic SL/TP: "EnterShortOrder:2:SL=24390:TP=24700"
        /// If UseFixedContracts=true, always returns DefaultContracts (ignores Claude's qty).
        /// </summary>
        private int ParseContracts(string cmd)
        {
            if (UseFixedContracts)
                return DefaultContracts;

            if (cmd.Contains(":"))
            {
                string[] parts = cmd.Split(':');
                if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int qty) && qty > 0 && qty <= 10)
                    return qty;
            }
            return DefaultContracts;
        }

        /// <summary>
        /// Parses SL and TP from atomic entry command.
        /// Format: "EnterLongOrder:1:SL=24390.00:TP=24700.00"
        /// </summary>
        private void ParseAtomicSLTP(string cmd)
        {
            pendingSL = 0;
            pendingTP = 0;
            string[] parts = cmd.Split(':');
            foreach (string part in parts)
            {
                string p = part.Trim();
                if (p.StartsWith("SL=", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(p.Substring(3).Replace(",", "."),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double sl))
                        pendingSL = sl;
                }
                else if (p.StartsWith("TP=", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(p.Substring(3).Replace(",", "."),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double tp))
                        pendingTP = tp;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0) return;

            List<string> cmds;
            lock (msgLock)
            {
                cmds = new List<string>(wsMsgs);
                wsMsgs.Clear();
            }

            foreach (string cmd in cmds)
            {
                if (cmd.Equals("PING_HEALTH_CHECK", StringComparison.OrdinalIgnoreCase))
                {
                    Send("PONG_HEALTH_CHECK");
                    continue;
                }

                if (cmd.StartsWith("EnterLongOrder", StringComparison.OrdinalIgnoreCase))
                {
                    int qty = ParseContracts(cmd);
                    ParseAtomicSLTP(cmd);
                    entryCounter++;
                    lastEntrySignal = "Buy" + entryCounter;
                    activeContracts = qty;

                    // Aplica SL/TP ANTES do EnterLong — NT associa ao signal automaticamente
                    if (pendingSL > 0)
                    {
                        SetStopLoss(lastEntrySignal, CalculationMode.Price, pendingSL, false);
                        stopLossSet = true; lastStopPrice = pendingSL;
                        Print($"[{Name}] Atomic SL set: {pendingSL}");
                    }
                    if (pendingTP > 0)
                    {
                        SetProfitTarget(lastEntrySignal, CalculationMode.Price, pendingTP);
                        profitTargetSet = true; lastProfitPrice = pendingTP;
                        Print($"[{Name}] Atomic TP set: {pendingTP}");
                    }

                    EnterLong(qty, lastEntrySignal);
                    Print($"[{Name}] LONG {qty} ctts signal: {lastEntrySignal}");
                    Send($"ORDER_SENT:Long;Qty={qty}");
                    if (pendingSL > 0) Send($"STOPLOSS_SET:{pendingSL.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    if (pendingTP > 0) Send($"PROFIT_TARGET_SET:{pendingTP.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                else if (cmd.StartsWith("EnterShortOrder", StringComparison.OrdinalIgnoreCase))
                {
                    int qty = ParseContracts(cmd);
                    ParseAtomicSLTP(cmd);
                    entryCounter++;
                    lastEntrySignal = "Sell" + entryCounter;
                    activeContracts = qty;

                    // Aplica SL/TP ANTES do EnterShort — NT associa ao signal automaticamente
                    if (pendingSL > 0)
                    {
                        SetStopLoss(lastEntrySignal, CalculationMode.Price, pendingSL, false);
                        stopLossSet = true; lastStopPrice = pendingSL;
                        Print($"[{Name}] Atomic SL set: {pendingSL}");
                    }
                    if (pendingTP > 0)
                    {
                        SetProfitTarget(lastEntrySignal, CalculationMode.Price, pendingTP);
                        profitTargetSet = true; lastProfitPrice = pendingTP;
                        Print($"[{Name}] Atomic TP set: {pendingTP}");
                    }

                    EnterShort(qty, lastEntrySignal);
                    Print($"[{Name}] SHORT {qty} ctts signal: {lastEntrySignal}");
                    Send($"ORDER_SENT:Short;Qty={qty}");
                    if (pendingSL > 0) Send($"STOPLOSS_SET:{pendingSL.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    if (pendingTP > 0) Send($"PROFIT_TARGET_SET:{pendingTP.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                else if (cmd.StartsWith("STOPLOSS:", StringComparison.OrdinalIgnoreCase))
                {
                    HandleStopLoss(cmd);
                }
                else if (cmd.StartsWith("BREAKEVEN", StringComparison.OrdinalIgnoreCase))
                {
                    HandleBreakeven(cmd);
                }
                else if (cmd.StartsWith("PROFIT:", StringComparison.OrdinalIgnoreCase))
                {
                    HandleProfitTarget(cmd);
                }
                else if (cmd.Equals("ExitOrders", StringComparison.OrdinalIgnoreCase))
                {
                    ExitLong();
                    ExitShort();
                    Print($"[{Name}] ExitOrders sent");
                }
                else if (cmd.Equals("CANCEL_PROFIT", StringComparison.OrdinalIgnoreCase))
                {
                    if (profitTargetSet)
                    {
                        // Set profit target to an extreme price that will never hit
                        // This effectively "cancels" the TP while keeping the order structure intact
                        EnsureSignalName();
                        double farPrice = Position.MarketPosition == MarketPosition.Long ? 99999.0 : 1.0;
                        SetProfitTarget(lastEntrySignal, CalculationMode.Price, farPrice);
                        profitTargetSet = false;
                        lastProfitPrice = 0;
                        confirmedProfitPrice = 0;
                        Print($"[{Name}] Profit target cancelled (set to {farPrice})");
                        Send("PROFIT_CANCELLED");
                    }
                    else
                    {
                        Print($"[{Name}] CANCEL_PROFIT: no profit target active");
                        Send("PROFIT_CANCELLED");
                    }
                }
                else if (cmd.Equals("CancelAllStops", StringComparison.OrdinalIgnoreCase))
                {
                    stopLossSet = false; lastStopPrice = 0; confirmedStopPrice = 0;
                    profitTargetSet = false; lastProfitPrice = 0; confirmedProfitPrice = 0;
                    Send("ALL_STOPS_CANCELLED");
                }
                else if (cmd.Equals("GetStatus", StringComparison.OrdinalIgnoreCase))
                {
                    SendStatus();
                }
                else
                {
                    Send($"ERROR:Unknown_Command;{cmd}");
                }
            }
        }

        private void HandleStopLoss(string cmd)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                Send("ERROR:No_Active_Position");
                return;
            }

            string raw = cmd.Split(new[] { ':' }, 2)[1].Trim();
            if (!double.TryParse(raw.Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double sl) || sl <= 0)
            {
                Send("ERROR:Invalid_StopLoss_Format");
                return;
            }

            // Validate stop is on correct side
            double price = Close[0];
            if (Position.MarketPosition == MarketPosition.Long && sl >= price)
            {
                Print($"[{Name}] SL {sl} >= price {price} for LONG — rejecting");
                Send($"ERROR:StopLoss_Wrong_Side;sl={sl};price={price}");
                return;
            }
            if (Position.MarketPosition == MarketPosition.Short && sl <= price)
            {
                Print($"[{Name}] SL {sl} <= price {price} for SHORT — rejecting");
                Send($"ERROR:StopLoss_Wrong_Side;sl={sl};price={price}");
                return;
            }

            try
            {
                EnsureSignalName();
                SetStopLoss(lastEntrySignal, CalculationMode.Price, sl, false);
                stopLossSet = true;
                lastStopPrice = sl;
                Print($"[{Name}] StopLoss set: {sl} for {Position.MarketPosition}");
                Send($"STOPLOSS_SET:{sl.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            catch (Exception ex)
            {
                Print($"[{Name}] SetStopLoss exception: {ex.Message}");
                Send($"ERROR:StopLoss_Exception:{ex.Message}");
            }
        }

        private void HandleBreakeven(string cmd)
        {
            if (Position.MarketPosition == MarketPosition.Flat || entryPrice == 0)
            {
                Send("ERROR:No_Active_Position");
                return;
            }

            double be = entryPrice;
            if (cmd.Contains(":"))
            {
                string raw = cmd.Split(new[] { ':' }, 2)[1].Trim();
                if (!double.TryParse(raw.Replace(",", "."),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out be))
                {
                    Send("ERROR:Invalid_Breakeven_Format");
                    return;
                }
            }

            // Validate stop is on correct side (same logic as HandleStopLoss)
            double price = Close[0];
            if (Position.MarketPosition == MarketPosition.Long && be >= price)
            {
                Print($"[{Name}] Breakeven {be} >= price {price} for LONG — using entryPrice {entryPrice}");
                be = entryPrice;
                if (be >= price)
                {
                    Send($"ERROR:Breakeven_Wrong_Side;be={be};price={price};side=LONG");
                    return;
                }
            }
            if (Position.MarketPosition == MarketPosition.Short && be <= price)
            {
                Print($"[{Name}] Breakeven {be} <= price {price} for SHORT — using entryPrice {entryPrice}");
                be = entryPrice;
                if (be <= price)
                {
                    Send($"ERROR:Breakeven_Wrong_Side;be={be};price={price};side=SHORT");
                    return;
                }
            }

            try
            {
                EnsureSignalName();
                double oldStop = lastStopPrice;
                SetStopLoss(lastEntrySignal, CalculationMode.Price, be, false);
                stopLossSet = true;
                lastStopPrice = be;
                Print($"[{Name}] Breakeven set at {be} (was {oldStop})");
                Send($"BREAKEVEN_SET:{be.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            catch (Exception ex)
            {
                Print($"[{Name}] Breakeven exception: {ex.Message}");
                Send($"ERROR:Breakeven_Exception:{ex.Message}");
            }
        }

        private void HandleProfitTarget(string cmd)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                Send("ERROR:No_Active_Position");
                return;
            }

            string raw = cmd.Split(new[] { ':' }, 2)[1].Trim();
            if (!double.TryParse(raw.Replace(",", "."),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double pt) || pt <= 0)
            {
                Send("ERROR:Invalid_Profit_Format");
                return;
            }

            try
            {
                EnsureSignalName();
                SetProfitTarget(lastEntrySignal, CalculationMode.Price, pt);
                profitTargetSet = true;
                lastProfitPrice = pt;
                Print($"[{Name}] ProfitTarget set at {pt}");
                Send($"PROFIT_TARGET_SET:{pt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            catch (Exception ex)
            {
                Print($"[{Name}] ProfitTarget exception: {ex.Message}");
                Send($"ERROR:ProfitTarget_Exception:{ex.Message}");
            }
        }

        /// <summary>
        /// Ensures lastEntrySignal is set. Required after Python restarts
        /// while NinjaTrader still has an active position.
        /// </summary>
        private void EnsureSignalName()
        {
            if (!string.IsNullOrEmpty(lastEntrySignal))
                return;

            entryCounter++;
            lastEntrySignal = Position.MarketPosition == MarketPosition.Long
                ? "Buy" + entryCounter
                : "Sell" + entryCounter;
            Print($"[{Name}] Recovered signal name: {lastEntrySignal}");
        }

        protected override void OnExecutionUpdate(Execution e, string id, double price, int qty, MarketPosition mp, string oId, DateTime t)
        {
            if (e.Order == null) return;

            // Entry fill — match by signal name OR by entry order type (fallback)
            bool isEntryFill = (e.Order.Name == lastEntrySignal && e.Order.OrderState == OrderState.Filled);
            // Fallback: if signal name doesn't match but it's an entry order that filled
            if (!isEntryFill && e.Order.OrderState == OrderState.Filled &&
                (e.Order.OrderAction == OrderAction.Buy || e.Order.OrderAction == OrderAction.SellShort) &&
                e.Order.AverageFillPrice > 0)
            {
                isEntryFill = true;
                Print($"[{Name}] Fill matched by OrderAction fallback (signal mismatch: order={e.Order.Name} expected={lastEntrySignal})");
            }

            if (isEntryFill && (e.Order.OrderAction == OrderAction.Buy || e.Order.OrderAction == OrderAction.SellShort))
            {
                entryPrice = e.Order.AverageFillPrice;
                activeContracts = e.Order.Quantity;
                entryTime = t;
                exitSide = mp == MarketPosition.Long ? "LONG" : "SHORT";
                positionCloseHandled = false;
                pendingExitReason = "";
                Print($"[{Name}] Filled: {mp} {activeContracts}@{entryPrice}");
                Send($"ORDER_UPDATE:OrderFilled;Price={entryPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)};Qty={activeContracts}");
                SendStatus();
                return;
            }

            // Exit fill — captura preco real de saida
            if (e.Order.OrderState == OrderState.Filled &&
                (e.Order.OrderAction == OrderAction.Sell || e.Order.OrderAction == OrderAction.BuyToCover))
            {
                exitPrice = e.Order.AverageFillPrice;
                Print($"[{Name}] Exit filled: {exitPrice} (order: {e.Order.Name})");
            }
        }

        protected override void OnOrderUpdate(Order o, double lim, double st, int qty, int filled, double avg, OrderState stt, DateTime time, ErrorCode err, string cmt)
        {
            // Track REAL stop/profit prices from NT order system
            if (o.Name.Contains("Stop") && (stt == OrderState.Working || stt == OrderState.Accepted))
            {
                if (st > 0)
                {
                    confirmedStopPrice = st;
                    Print($"[{Name}] Stop order CONFIRMED by NT: {st} (state={stt})");
                }
            }
            if (o.Name.Contains("Profit") && (stt == OrderState.Working || stt == OrderState.Accepted))
            {
                if (lim > 0)
                {
                    confirmedProfitPrice = lim;
                    Print($"[{Name}] Profit order CONFIRMED by NT: {lim} (state={stt})");
                }
            }

            // Captura exit reason e exit price do order fill (antes do OnPositionUpdate)
            if ((o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.BuyToCover) &&
                stt == OrderState.Filled)
            {
                pendingExitReason = o.Name.Contains("Stop") ? "STOP_LOSS"
                    : o.Name.Contains("Profit") ? "TAKE_PROFIT"
                    : "MANUAL";

                // Fallback: se OnExecutionUpdate nao capturou exitPrice, usa avg do order
                if (exitPrice == 0)
                    exitPrice = avg;

                Print($"[{Name}] Order filled: {o.Name} avg={avg} reason={pendingExitReason}");
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            // ── Posicao ABRE (Long/Short) ──
            if (marketPosition != MarketPosition.Flat)
            {
                positionCloseHandled = false;
                // SEMPRE salva averagePrice — unico momento que temos esse valor
                lastPositionAvgPrice = averagePrice;
                if (entryPrice == 0 && averagePrice > 0)
                    entryPrice = averagePrice;
                if (string.IsNullOrEmpty(exitSide))
                    exitSide = marketPosition == MarketPosition.Long ? "LONG" : "SHORT";
                if (activeContracts == 0)
                    activeContracts = Math.Max(quantity, 1);
                Print($"[{Name}] Position opened: {marketPosition} qty={quantity} avg={averagePrice} entryPrice={entryPrice}");
                return;
            }

            // ── Posicao FECHA (Flat) ──
            if (positionCloseHandled) return;
            positionCloseHandled = true;
            DateTime now = DateTime.Now;

            // Monta dados com cascata de fallbacks
            double realExit = exitPrice > 0 ? exitPrice : Close[0];
            double realEntry = entryPrice > 0 ? entryPrice
                             : lastPositionAvgPrice > 0 ? lastPositionAvgPrice
                             : 0;
            int ctts = activeContracts > 0 ? activeContracts : Math.Max(quantity, 1);
            string side = !string.IsNullOrEmpty(exitSide) ? exitSide : "UNKNOWN";
            string reason = !string.IsNullOrEmpty(pendingExitReason) ? pendingExitReason : "UNKNOWN";

            Print($"[{Name}] Position FLAT — entryPrice={entryPrice} lastAvg={lastPositionAvgPrice} exitPrice={exitPrice} side={side} reason={reason}");

            if (realEntry > 0)
            {
                double pnl;
                if (side == "LONG")
                    pnl = (realExit - realEntry) * 2.0 * ctts;
                else if (side == "SHORT")
                    pnl = (realEntry - realExit) * 2.0 * ctts;
                else
                    pnl = 0.0;

                Print($"[{Name}] Trade closed — entry={realEntry} exit={realExit} side={side} ctts={ctts} PnL=${pnl:F2} reason={reason}");
                Send($"POSITION_CLOSED:Flat;EntryPrice={realEntry.ToString(System.Globalization.CultureInfo.InvariantCulture)};ExitPrice={realExit.ToString(System.Globalization.CultureInfo.InvariantCulture)};PnL={pnl.ToString(System.Globalization.CultureInfo.InvariantCulture)};Side={side};Qty={ctts};Reason={reason}");
                WriteTradeLog(realEntry, realExit, side, ctts, pnl, reason,
                    entryTime != DateTime.MinValue ? entryTime : now, now);
            }
            else
            {
                Print($"[{Name}] WARNING: No entry data — entryPrice={entryPrice} lastAvg={lastPositionAvgPrice}");
            }

            Send("EXITED_ALL_POSITIONS");

            // Reset state
            stopLossSet = profitTargetSet = false;
            lastStopPrice = lastProfitPrice = entryPrice = 0;
            confirmedStopPrice = confirmedProfitPrice = 0;
            exitPrice = 0;
            exitSide = "";
            pendingExitReason = "";
            activeContracts = 0;
            lastEntrySignal = null;
            entryTime = DateTime.MinValue;
            lastPositionAvgPrice = 0;
        }

        private void WriteTradeLog(double entry, double exit, string side, int ctts, double pnl, string reason, DateTime tradeEntryTime, DateTime tradeExitTime)
        {
            try
            {
                bool fileExists = File.Exists(TradeLogPath);
                using (var sw = new StreamWriter(TradeLogPath, append: true))
                {
                    if (!fileExists)
                        sw.WriteLine("date,time_entry,time_exit,side,contracts,entry_price,exit_price,pnl,exit_reason");

                    sw.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5:F2},{6:F2},{7:F2},{8}",
                        tradeExitTime.ToString("yyyy-MM-dd"),
                        tradeEntryTime.ToString("HH:mm:ss"),
                        tradeExitTime.ToString("HH:mm:ss"),
                        side,
                        ctts,
                        entry,
                        exit,
                        pnl,
                        reason));
                }
                Print($"[{Name}] Trade logged to {TradeLogPath}");
            }
            catch (Exception ex)
            {
                Print($"[{Name}] Error writing trade log: {ex.Message}");
            }
        }

        private void SendStatus()
        {
            // Use best available entry price: our tracked value, or NT's AveragePrice as fallback
            double reportedEntry = entryPrice > 0 ? entryPrice
                : Position.MarketPosition != MarketPosition.Flat ? Position.AveragePrice
                : 0;
            int reportedQty = activeContracts > 0 ? activeContracts
                : Position.MarketPosition != MarketPosition.Flat ? Math.Max(Position.Quantity, 1)
                : 0;

            // Use CONFIRMED prices from OnOrderUpdate (real NT orders) over cached lastStopPrice
            double reportedStop = confirmedStopPrice > 0 ? confirmedStopPrice : lastStopPrice;
            double reportedProfit = confirmedProfitPrice > 0 ? confirmedProfitPrice : lastProfitPrice;

            // Log divergence between cached and confirmed prices
            if (confirmedStopPrice > 0 && Math.Abs(confirmedStopPrice - lastStopPrice) > 0.5)
            {
                Print($"[{Name}] STOP DIVERGENCE: cached={lastStopPrice} confirmed={confirmedStopPrice} — reporting confirmed");
            }

            Send(
                $"STATUS:InPosition={(Position.MarketPosition != MarketPosition.Flat)};" +
                $"PositionType={Position.MarketPosition};" +
                $"EntryPrice={reportedEntry.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                $"NTPosition={Position.MarketPosition};" +
                $"Quantity={reportedQty};" +
                $"StopLossSet={stopLossSet};StopPrice={reportedStop.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                $"ProfitTargetSet={profitTargetSet};ProfitPrice={reportedProfit.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                $"CurrentPrice={Close[0].ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            );
        }

        #region WebSocket Server
        private void StartWs()
        {
            var uri = new Uri(ServerAddress);
            http = new HttpListener();
            http.Prefixes.Add($"http://{uri.Host}:{uri.Port}/");
            http.Start();
            _ = Task.Run(() => LoopAccept(cts.Token));
            Print($"[{Name}] WS listening at {uri.Port}");
        }

        private async Task LoopAccept(CancellationToken tk)
        {
            while (!tk.IsCancellationRequested && http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); }
                catch { break; }

                if (ctx.Request.IsWebSocketRequest)
                    _ = HandleWs(ctx, tk);
                else { ctx.Response.StatusCode = 400; ctx.Response.Close(); }
            }
        }

        private async Task HandleWs(HttpListenerContext ctx, CancellationToken tk)
        {
            WebSocket ws = null;
            try
            {
                ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                lock (cliLock) clients.Add(ws);
                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("CONNECTION_OK")),
                                   WebSocketMessageType.Text, true, tk);
                await LoopRecv(ws, tk);
            }
            catch { }
            finally { if (ws != null) { lock (cliLock) clients.Remove(ws); ws.Dispose(); } }
        }

        private async Task LoopRecv(WebSocket ws, CancellationToken tk)
        {
            var buf = new byte[512];
            try
            {
                while (ws.State == WebSocketState.Open && !tk.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), tk);
                    if (r.MessageType == WebSocketMessageType.Text)
                    {
                        var m = Encoding.UTF8.GetString(buf, 0, r.Count);
                        lock (msgLock) wsMsgs.Add(m.Trim());
                    }
                    else if (r.MessageType == WebSocketMessageType.Close)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", tk);
                }
            }
            catch (Exception ex)
            {
                Print($"[{Name}] LoopRecv exception: {ex.Message}");
            }
        }

        private void StopWs()
        {
            try { http?.Stop(); http?.Close(); } catch { }
            lock (cliLock)
            {
                foreach (var ws in clients)
                    try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None).Wait(200); }
                    catch { }
                clients.Clear();
            }
        }

        private void Send(string msg)
        {
            var seg = new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg));
            lock (cliLock)
            {
                foreach (var ws in clients)
                    if (ws.State == WebSocketState.Open)
                        _ = ws.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        #endregion

        public void Dispose() { StopWs(); cts?.Dispose(); }
    }
}
