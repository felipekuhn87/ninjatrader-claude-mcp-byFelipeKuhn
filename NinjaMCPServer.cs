using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators.Kuhns
{
    /// <summary>
    /// NinjaMCPServer — WebSocket data server exposto ao Claude Code via ninjatrader-mcp (Node.js).
    /// Porta padrão: 8000. Expõe: OHLCV bars, Quote (bid/ask/last), Indicators discovery
    /// (clones dinâmicos via reflection), Time &amp; Sales. Para receber Market Data tick-a-tick,
    /// Calculate = OnEachTick é obrigatório.
    /// </summary>
    public class NinjaMCPServer : Indicator, IDisposable
    {
        private HttpListener httpListener;
        private CancellationTokenSource cts;
        private readonly List<WebSocket> clients = new List<WebSocket>();
        private readonly object clientsLock = new object();
        private readonly List<BarData> chartBars = new List<BarData>();
        private readonly object barsLock = new object();

        private class BarData
        {
            public DateTime Timestamp { get; set; }
            public double Open  { get; set; }
            public double High  { get; set; }
            public double Low   { get; set; }
            public double Close { get; set; }
            public long Volume  { get; set; }
        }

        // ── Time & Sales + Quote tracking ──
        private class TickData
        {
            public DateTime Time { get; set; }
            public double Price { get; set; }
            public long Volume { get; set; }
            public string Type { get; set; } // "Last", "Bid", "Ask"
        }

        private readonly Queue<TickData> ticksBuffer = new Queue<TickData>();
        private readonly object ticksLock = new object();
        private const int MaxTicks = 500;

        private double lastBid = 0;
        private double lastAsk = 0;
        private double lastTrade = 0;
        private long lastVolume = 0;

        // ── Indicator snapshots ──
        // Snapshots são alimentados dentro de OnBarUpdate a partir das instâncias DESTE indicator
        // (NinjaTrader.NinjaScript.Indicators.SMA, EMA, RSI, MACD criadas via factory methods),
        // garantindo que os valores correspondem ao data series corrente do chart.
        // ChartControl.Indicators é unreliable depois de Reload Historical (bug do NT que mantém
        // os indicators bindados ao buffer antigo).
        private class PlotHistory
        {
            public string Name { get; set; }
            public double Current { get; set; } = 0;        // valor live (atualizado a cada tick)
            public Queue<double> Closed { get; set; } = new Queue<double>(); // bars fechados, mais recente no fim
        }
        private class IndicatorSnap
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public List<PlotHistory> Plots { get; set; } = new List<PlotHistory>();
        }
        private readonly Dictionary<string, IndicatorSnap> indSnaps = new Dictionary<string, IndicatorSnap>();
        private readonly object indSnapsLock = new object();
        private const int MaxIndHistory = 100; // últimos 100 bars fechados

        // ── Clones dinâmicos dos indicators do chart ──
        // Bug NT: ChartControl.Indicators retorna instances bindadas a buffer cached/stale após reload.
        // Workaround: lemos a LISTA + PARÂMETROS dos chart indicators via reflection, e criamos
        // CLONES próprios via factory methods (SMA(period), EMA(period), etc) — esses clones ficam
        // bindados ao MEU data series (corrente) e seus Values são confiáveis.
        private class IndicatorClone
        {
            public string TypeName;       // "SMA", "EMA", "RSI", "MACD"...
            public string DisplayName;    // "SMA(MNQ JUN26 (5 Minute),21)"
            public Indicator Clone;       // instância clonada (bindada ao MEU Bars)
            public List<string> PlotNames = new List<string>();
        }
        private readonly List<IndicatorClone> clonedIndicators = new List<IndicatorClone>();
        private readonly object clonesLock = new object();
        private bool clonesInitialized = false;

        [NinjaScriptProperty]
        [Display(Name="Server Address", GroupName="Parameters", Order=1)]
        public string ServerAddress { get; set; }

        [NinjaScriptProperty]
        [Range(1, 150000)]
        [Display(Name="Max Stored Bars", GroupName="Parameters", Order=2)]
        public int MaxStoredBars { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "NinjaMCPServer";
                Description      = "MCP WebSocket data server — OHLCV, Quote, Indicators (dynamic clones), Time&Sales para Claude Code";
                // OnBarClose: OnBarUpdate roda 1x por bar fechado (leve).
                // OnMarketData e OnMarketDepth disparam per-tick INDEPENDENTE do Calculate
                // (vindo dos próprios callbacks nativos), então quote/T&S continuam em tempo real.
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                DisplayInDataBox = false;
                ServerAddress    = "ws://localhost:8000/ws";
                MaxStoredBars    = 1000;
            }
            else if (State == State.Configure)
            {
                cts = new CancellationTokenSource();
                StartWebSocketServer();
            }
            else if (State == State.DataLoaded)
            {
                Print($"[{Name}] State.DataLoaded — Bars.Count={(Bars != null ? Bars.Count : -1)} CurrentBar={CurrentBar}");
                // chartBars será populado naturalmente em OnBarUpdate durante processamento histórico
                // Indicators serão clonados lazy na primeira OnBarUpdate (ChartControl ainda null aqui)
            }
            else if (State == State.Historical)
            {
                Print($"[{Name}] State.Historical — Bars.Count={(Bars != null ? Bars.Count : -1)}");
            }
            else if (State == State.Transition)
            {
                Print($"[{Name}] State.Transition — Bars.Count={(Bars != null ? Bars.Count : -1)} chartBars={chartBars.Count} indSnaps={indSnaps.Count}");
                // Capturar histórico antes da transição terminar
                CaptureHistoricalBars();
            }
            else if (State == State.Realtime)
            {
                Print($"[{Name}] State.Realtime — Bars.Count={(Bars != null ? Bars.Count : -1)} chartBars={chartBars.Count} indSnaps={indSnaps.Count}");
            }
            else if (State == State.Terminated)
            {
                try
                {
                    cts?.Cancel();
                    StopWebSocketServer();
                }
                catch (Exception ex)
                {
                    Print($"Erro ao terminar: {ex.Message}");
                }
            }
        }

        // OnBarUpdate roda 1x por bar FECHADO (Calculate.OnBarClose).
        // Aqui populamos o chartBars com o bar que acabou de fechar, snapshotamos indicators
        // (no contexto correto, CurrentBar válido), refreshamos clones se user mudou o chart
        // e broadcastamos update pra clientes WS conectados.
        // Durante o bar em formação, chartBars[0] é o último bar FECHADO. Pra preço live,
        // o cliente usa nt_get_quote (alimentado por OnMarketData per-tick).
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0) return;

            var closedBar = new BarData {
                Timestamp = Time[0], Open = Open[0],
                High = High[0], Low = Low[0],
                Close = Close[0], Volume = (long)Volume[0]
            };

            lock (barsLock)
            {
                chartBars.Insert(0, closedBar);
                if (chartBars.Count > MaxStoredBars)
                    chartBars.RemoveAt(chartBars.Count - 1);
            }

            // Uma vez por bar: checar se user adicionou/removeu indicator no chart
            RefreshClonesIfChanged();

            // Snapshot dos indicator clones no contexto correto (CurrentBar válido)
            SnapshotIndicators();

            // Broadcast update pra clientes WS
            if (clients.Count == 0) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string data = $"{closedBar.Timestamp:yyyyMMdd HHmmss} {closedBar.Open.ToString("F2", inv)} {closedBar.High.ToString("F2", inv)} {closedBar.Low.ToString("F2", inv)} {closedBar.Close.ToString("F2", inv)} {closedBar.Volume}";
            string msg = "UPDATE;" + data;
            List<WebSocket> snapshot;
            lock (clientsLock) snapshot = clients.ToList();
            foreach (var ws in snapshot)
                if (ws.State == WebSocketState.Open)
                    _ = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)),
                                     WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // Capturar histórico de barras durante transição
        private void CaptureHistoricalBars()
        {
            try
            {
                lock (barsLock)
                {
                    int toProcess = Math.Min(Bars.Count, MaxStoredBars);
                    Print($"[{Name}] Capturando {toProcess} barras históricas do Bars");

                    // Adicionar do mais antigo para o mais recente (índice decrescente)
                    for (int i = toProcess - 1; i >= 0; i--)
                    {
                        try
                        {
                            var b = new BarData
                            {
                                Timestamp = Time[i],
                                Open = Open[i],
                                High = High[i],
                                Low = Low[i],
                                Close = Close[i],
                                Volume = (long)Volume[i]
                            };
                            chartBars.Insert(0, b);
                            if (chartBars.Count > MaxStoredBars)
                                chartBars.RemoveAt(chartBars.Count - 1);
                        }
                        catch (Exception ex)
                        {
                            Print($"[ERROR] Captura barra {i}: {ex.Message}");
                        }
                    }

                    Print($"[{Name}] Histórico carregado: {chartBars.Count} barras");
                }
            }
            catch (Exception ex)
            {
                Print($"[ERROR] CaptureHistoricalBars: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Time & Sales + Quote
        // ────────────────────────────────────────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            var tick = new TickData
            {
                Time = e.Time,
                Price = e.Price,
                Volume = e.Volume,
            };

            if (e.MarketDataType == MarketDataType.Last)
            {
                tick.Type = "Last";
                lastTrade = e.Price;
                lastVolume = e.Volume;
            }
            else if (e.MarketDataType == MarketDataType.Bid)
            {
                tick.Type = "Bid";
                lastBid = e.Price;
            }
            else if (e.MarketDataType == MarketDataType.Ask)
            {
                tick.Type = "Ask";
                lastAsk = e.Price;
            }
            else
            {
                return;
            }

            lock (ticksLock)
            {
                ticksBuffer.Enqueue(tick);
                while (ticksBuffer.Count > MaxTicks)
                    ticksBuffer.Dequeue();
            }
        }

        private string GetQuoteJson()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return $"QUOTE:{{\"bid\":{lastBid.ToString("F2", inv)},\"ask\":{lastAsk.ToString("F2", inv)},\"last\":{lastTrade.ToString("F2", inv)},\"volume\":{lastVolume}}}";
        }

        private string GetTimeSalesJson(int count)
        {
            var sb = new StringBuilder();
            sb.Append("TIMESALES:[");

            lock (ticksLock)
            {
                var arr = ticksBuffer.ToArray();
                int start = Math.Max(0, arr.Length - count);
                bool first = true;
                for (int i = start; i < arr.Length; i++)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    var t = arr[i];
                    sb.Append($"{{\"time\":\"{t.Time:HH:mm:ss.fff}\",\"type\":\"{t.Type}\",\"price\":{t.Price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},\"volume\":{t.Volume}}}");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────
        // Snapshot de indicators (rodado de dentro de OnBarUpdate, contexto correto)
        // ────────────────────────────────────────────────────────────
        // ────────────────────────────────────────────────────────────
        // Clone dinâmico de indicators (reflection)
        // ────────────────────────────────────────────────────────────
        private void InitializeClones()
        {
            if (clonesInitialized) return;
            if (ChartControl == null || ChartControl.Indicators == null) return;

            lock (clonesLock)
            {
                if (clonesInitialized) return;

                int total = 0, ok = 0, skipped = 0;
                foreach (var ind in ChartControl.Indicators)
                {
                    if (ind == null) continue;
                    if (ind.Name == this.Name) continue; // skip self
                    total++;

                    try
                    {
                        var clone = TryCloneIndicator(ind);
                        if (clone == null)
                        {
                            Print($"[{Name}] CLONE skip: {ind.Name} (no factory match)");
                            skipped++;
                            continue;
                        }

                        var ic = new IndicatorClone
                        {
                            TypeName = ind.GetType().Name,
                            DisplayName = ind.DisplayName ?? ind.Name,
                            Clone = clone,
                        };
                        if (ind.Plots != null)
                        {
                            for (int p = 0; p < ind.Plots.Length; p++)
                                ic.PlotNames.Add(ind.Plots[p].Name ?? ("Plot" + p));
                        }
                        clonedIndicators.Add(ic);
                        Print($"[{Name}] CLONE ok: {ind.GetType().Name} → {ind.DisplayName} ({ic.PlotNames.Count} plots)");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Print($"[{Name}] CLONE error {ind.Name}: {ex.Message}");
                        skipped++;
                    }
                }

                clonesInitialized = true;
                Print($"[{Name}] InitializeClones done: total={total} cloned={ok} skipped={skipped}");
            }
        }

        // Detecta diff entre ChartControl.Indicators e clonedIndicators e sincroniza:
        // - Adiciona clones para indicators novos
        // - Remove clones de indicators que sumiram do chart
        // - Preserva clones existentes (mantém Closed history acumulado)
        // Chamado a cada IsFirstTickOfBar.
        private void RefreshClonesIfChanged()
        {
            if (!clonesInitialized) return; // primeira inicialização ainda não rodou
            if (ChartControl == null || ChartControl.Indicators == null) return;

            try
            {
                // Snapshot dos display names atuais no chart (excluindo self)
                var chartNames = new HashSet<string>();
                var chartObjs = new Dictionary<string, object>();
                foreach (var ind in ChartControl.Indicators)
                {
                    if (ind == null) continue;
                    if (ind.Name == this.Name) continue;
                    string dn = ind.DisplayName ?? ind.Name;
                    chartNames.Add(dn);
                    chartObjs[dn] = ind;
                }

                lock (clonesLock)
                {
                    // Existing clones por nome
                    var cloneNames = new HashSet<string>(clonedIndicators.Select(ic => ic.DisplayName));

                    // Detecta diff
                    var toRemove = clonedIndicators.Where(ic => !chartNames.Contains(ic.DisplayName)).ToList();
                    var toAdd = chartObjs.Where(kv => !cloneNames.Contains(kv.Key)).ToList();

                    if (toRemove.Count == 0 && toAdd.Count == 0) return; // nada mudou

                    // Remove clones zumbis
                    foreach (var ic in toRemove)
                    {
                        clonedIndicators.Remove(ic);
                        Print($"[{Name}] CLONE removed (no longer on chart): {ic.DisplayName}");
                    }
                    // Limpa snapshots órfãos
                    if (toRemove.Count > 0)
                    {
                        lock (indSnapsLock)
                        {
                            foreach (var ic in toRemove)
                                indSnaps.Remove(ic.DisplayName);
                        }
                    }

                    // Adiciona novos
                    foreach (var kv in toAdd)
                    {
                        var ind = kv.Value;
                        try
                        {
                            var clone = TryCloneIndicator(ind);
                            if (clone == null)
                            {
                                Print($"[{Name}] CLONE skip (refresh): {kv.Key} (no factory match)");
                                continue;
                            }

                            var ic = new IndicatorClone
                            {
                                TypeName = ind.GetType().Name,
                                DisplayName = kv.Key,
                                Clone = clone,
                            };
                            // Coleta plot names via reflection (já que ind é object)
                            var plotsProp = ind.GetType().GetProperty("Plots");
                            if (plotsProp != null)
                            {
                                var plotsArr = plotsProp.GetValue(ind, null) as Array;
                                if (plotsArr != null)
                                {
                                    foreach (var plot in plotsArr)
                                    {
                                        var nameProp = plot.GetType().GetProperty("Name");
                                        var pname = nameProp != null ? (string)nameProp.GetValue(plot, null) : "Plot";
                                        ic.PlotNames.Add(pname ?? "Plot");
                                    }
                                }
                            }
                            clonedIndicators.Add(ic);
                            Print($"[{Name}] CLONE added (refresh): {ind.GetType().Name} → {kv.Key} ({ic.PlotNames.Count} plots)");
                        }
                        catch (Exception ex)
                        {
                            Print($"[{Name}] CLONE refresh error {kv.Key}: {ex.Message}");
                        }
                    }

                    Print($"[{Name}] Refresh done: -{toRemove.Count} +{toAdd.Count} (total clones: {clonedIndicators.Count})");
                }
            }
            catch (Exception ex)
            {
                Print($"[{Name}] RefreshClones error: {ex.Message}");
            }
        }

        // Usa reflection pra encontrar o factory method do tipo no Indicator base e invocar com os
        // mesmos parâmetros [NinjaScriptProperty] da instância original. Isso clona o indicator
        // bindado ao MEU data series (Close por padrão), garantindo Values corretos.
        // Param é object porque ChartControl.Indicators retorna IndicatorRenderBase, não Indicator —
        // e precisamos só de GetType() + reflection, que funcionam em qualquer tipo.
        private Indicator TryCloneIndicator(object original)
        {
            if (original == null) return null;
            var origType = original.GetType();
            var typeName = origType.Name;

            // Coleta props marcadas com [NinjaScriptProperty] na ordem de declaração
            var props = origType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.IsDefined(typeof(NinjaScriptPropertyAttribute), true))
                .OrderBy(p => p.MetadataToken)
                .ToList();

            object[] values = props.Select(p => p.GetValue(original, null)).ToArray();
            Type[] types = values.Select(v => v != null ? v.GetType() : typeof(object)).ToArray();

            var thisType = this.GetType();

            // 1) Tenta factory direto: e.g. SMA(int period)
            var method = thisType.GetMethod(typeName, types);
            if (method != null && typeof(Indicator).IsAssignableFrom(method.ReturnType))
                return (Indicator)method.Invoke(this, values);

            // 2) Tenta factory com input series prepended: e.g. SMA(ISeries<double>, int)
            var typesWithInput = new Type[] { typeof(ISeries<double>) }.Concat(types).ToArray();
            var valuesWithInput = new object[] { (ISeries<double>)Close }.Concat(values).ToArray();
            method = thisType.GetMethod(typeName, typesWithInput);
            if (method != null && typeof(Indicator).IsAssignableFrom(method.ReturnType))
                return (Indicator)method.Invoke(this, valuesWithInput);

            // 3) Tenta factory parameterless (raro)
            method = thisType.GetMethod(typeName, Type.EmptyTypes);
            if (method != null && typeof(Indicator).IsAssignableFrom(method.ReturnType))
                return (Indicator)method.Invoke(this, null);

            return null;
        }

        // ────────────────────────────────────────────────────────────
        // Snapshot dos clones (rodado dentro de OnBarUpdate)
        // ────────────────────────────────────────────────────────────
        private void SnapshotIndicators()
        {
            if (CurrentBar < 1) return;

            // Lazy init dos clones — primeira OnBarUpdate onde ChartControl está disponível
            if (!clonesInitialized)
            {
                InitializeClones();
                if (!clonesInitialized) return;
            }

            try
            {
                lock (indSnapsLock)
                {
                    foreach (var ic in clonedIndicators)
                    {
                        IndicatorSnap snap;
                        if (!indSnaps.TryGetValue(ic.DisplayName, out snap))
                        {
                            snap = new IndicatorSnap { Name = ic.TypeName, DisplayName = ic.DisplayName };
                            for (int p = 0; p < ic.PlotNames.Count; p++)
                                snap.Plots.Add(new PlotHistory { Name = ic.PlotNames[p] });
                            indSnaps[ic.DisplayName] = snap;
                        }

                        for (int p = 0; p < ic.PlotNames.Count && p < snap.Plots.Count; p++)
                        {
                            if (ic.Clone.Values == null || ic.Clone.Values.Length <= p) continue;
                            var ser = ic.Clone.Values[p];
                            if (ser == null || ser.Count == 0) continue;

                            double cur = 0;
                            try { cur = ser[0]; } catch { }
                            snap.Plots[p].Current = cur;

                            if (IsFirstTickOfBar && ser.Count >= 2)
                            {
                                double closed = 0;
                                try { closed = ser[1]; } catch { }
                                snap.Plots[p].Closed.Enqueue(closed);
                                while (snap.Plots[p].Closed.Count > MaxIndHistory)
                                    snap.Plots[p].Closed.Dequeue();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"[{Name}] SnapshotIndicators error: {ex.Message}");
            }
        }

        // ────────────────────────────────────────────────────────────
        // Indicators discovery (lê do snapshot, NÃO de ind.Values direto)
        // ────────────────────────────────────────────────────────────
        // Lê do snapshot (PlotHistory) populado em SnapshotIndicators().
        // bars=1 → "value":Current     | bars>1 → "values":[Current, Closed[n-1], Closed[n-2], ...]
        // values[0] sempre = bar atual (live), depois bars fechados em ordem decrescente.
        private string GetAllIndicatorsJson(int bars = 1)
        {
            if (bars < 1) bars = 1;
            if (bars > MaxIndHistory + 1) bars = MaxIndHistory + 1;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("INDICATORS:[");

            lock (indSnapsLock)
            {
                bool first = true;
                foreach (var kv in indSnaps)
                {
                    var snap = kv.Value;
                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("{");
                    sb.Append($"\"name\":\"{EscapeJson(snap.Name)}\",");
                    sb.Append($"\"displayName\":\"{EscapeJson(snap.DisplayName)}\",");
                    sb.Append("\"plots\":[");

                    bool firstPlot = true;
                    foreach (var ph in snap.Plots)
                    {
                        if (string.IsNullOrEmpty(ph.Name)) continue;
                        if (!firstPlot) sb.Append(",");
                        firstPlot = false;

                        sb.Append("{");
                        sb.Append($"\"name\":\"{EscapeJson(ph.Name)}\",");
                        if (bars == 1)
                        {
                            sb.Append($"\"value\":{ph.Current.ToString("F4", inv)}");
                        }
                        else
                        {
                            sb.Append("\"values\":[");
                            sb.Append(ph.Current.ToString("F4", inv));
                            int n = bars - 1; // já incluímos current
                            var arr = ph.Closed.ToArray(); // [oldest..newest]
                            int take = Math.Min(n, arr.Length);
                            for (int i = 0; i < take; i++)
                            {
                                sb.Append(",");
                                // do mais recente fechado pro mais antigo
                                sb.Append(arr[arr.Length - 1 - i].ToString("F4", inv));
                            }
                            sb.Append("]");
                        }
                        sb.Append("}");
                    }
                    sb.Append("]}");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        private string GetIndicatorValue(string indicatorName, int bars = 1)
        {
            if (bars < 1) bars = 1;
            if (bars > MaxIndHistory + 1) bars = MaxIndHistory + 1;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            lock (indSnapsLock)
            {
                IndicatorSnap match = null;
                foreach (var kv in indSnaps)
                {
                    if (kv.Value.Name.Equals(indicatorName, StringComparison.OrdinalIgnoreCase) ||
                        (kv.Value.DisplayName != null && kv.Value.DisplayName.Equals(indicatorName, StringComparison.OrdinalIgnoreCase)))
                    {
                        match = kv.Value;
                        break;
                    }
                }
                if (match == null) return $"ERROR:Indicator_Not_Found:{indicatorName}";

                var sb = new StringBuilder();
                sb.Append($"INDICATOR:{match.Name}:{{");
                bool first = true;
                foreach (var ph in match.Plots)
                {
                    if (string.IsNullOrEmpty(ph.Name)) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append($"\"{ph.Name}\":");
                    if (bars == 1)
                    {
                        sb.Append(ph.Current.ToString("F4", inv));
                    }
                    else
                    {
                        sb.Append("[");
                        sb.Append(ph.Current.ToString("F4", inv));
                        int n = bars - 1;
                        var arr = ph.Closed.ToArray();
                        int take = Math.Min(n, arr.Length);
                        for (int i = 0; i < take; i++)
                        {
                            sb.Append(",");
                            sb.Append(arr[arr.Length - 1 - i].ToString("F4", inv));
                        }
                        sb.Append("]");
                    }
                }
                sb.Append("}");
                return sb.ToString();
            }
        }

        private static string EscapeJson(string s)
        {
            return s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        }

        // ────────────────────────────────────────────────────────────
        // WebSocket server
        // ────────────────────────────────────────────────────────────
        private void StartWebSocketServer()
        {
            try
            {
                var uri    = new Uri(ServerAddress);
                var scheme = uri.Scheme == "wss" ? "https" : "http";
                var prefix = $"{scheme}://{uri.Host}:{uri.Port}/";

                httpListener = new HttpListener();
                httpListener.Prefixes.Add(prefix);
                httpListener.Start();
                Task.Run(() => AcceptLoop(cts.Token));
                Print($"[{Name}] WebSocket ouvindo em {prefix}");
            }
            catch (Exception ex)
            {
                Print($"Error starting WebSocket server: {ex.Message}");
            }
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && httpListener?.IsListening == true)
            {
                HttpListenerContext ctx = null;
                try { ctx = await httpListener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }

                if (ctx.Request.IsWebSocketRequest)
                    ProcessRequest(ctx, token);
                else
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                }
            }
        }

        private async void ProcessRequest(HttpListenerContext ctx, CancellationToken token)
        {
            WebSocket ws = null;
            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
                ws = wsCtx.WebSocket;
                lock (clientsLock) clients.Add(ws);
                Print($"[{Name}] Cliente conectado (clients={clients.Count})");

                // Hello message com defesa total — se Bars/Instrument der throw, ainda envia CONNECTION_OK
                string hello = "CONNECTION_OK";
                try
                {
                    if (Bars != null && Bars.Instrument != null && Bars.Instrument.MasterInstrument != null)
                    {
                        hello += $"|{Bars.Instrument.MasterInstrument.Name}|{BarsPeriod}";
                    }
                    else
                    {
                        hello += "|NO_INSTRUMENT|NO_PERIOD";
                        Print($"[{Name}] WARNING: Bars/Instrument null no hello (instance órfão?)");
                    }
                }
                catch (Exception helloEx)
                {
                    Print($"[{Name}] Hello build exception: {helloEx.Message}");
                    hello += "|ERROR|ERROR";
                }
                await SendMessageAsync(ws, hello).ConfigureAwait(false);

                await ReceiveLoop(ws, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Print($"[{Name}] Handshake error: {ex.Message}");
            }
            finally
            {
                if (ws != null)
                {
                    lock (clientsLock) clients.Remove(ws);
                    ws.Dispose();
                    Print($"[{Name}] Cliente desconectado (clients={clients.Count})");
                }
            }
        }

        private async Task ReceiveLoop(WebSocket ws, CancellationToken token)
        {
            var buf = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), token).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buf, 0, res.Count).Trim();
                        Print($"Mensagem recebida: {msg}");

                        // ── Health check ──
                        if (msg.Equals("PING_HEALTH_CHECK", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageAsync(ws, "PONG_HEALTH_CHECK");
                            continue;
                        }

                        // ── Quote ──
                        if (msg.Equals("QUOTE", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageAsync(ws, GetQuoteJson());
                            continue;
                        }

                        // ── Time & Sales ──
                        if (msg.StartsWith("TIMESALES:", StringComparison.OrdinalIgnoreCase))
                        {
                            string param = msg.Substring("TIMESALES:".Length).Trim();
                            int cnt = 50;
                            int.TryParse(param, out cnt);
                            await SendMessageAsync(ws, GetTimeSalesJson(cnt));
                            continue;
                        }

                        // ── Indicators discovery (all) ──
                        // INDICATORS         → bars=1 (current value)
                        // INDICATORS:5       → bars=5 (last 5 values per plot)
                        if (msg.Equals("INDICATORS", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageAsync(ws, GetAllIndicatorsJson(1));
                            continue;
                        }
                        if (msg.StartsWith("INDICATORS:", StringComparison.OrdinalIgnoreCase))
                        {
                            string p = msg.Substring("INDICATORS:".Length).Trim();
                            int bars = 1;
                            int.TryParse(p, out bars);
                            await SendMessageAsync(ws, GetAllIndicatorsJson(bars));
                            continue;
                        }

                        // ── Single indicator ──
                        // INDICATOR:RSI       → bars=1
                        // INDICATOR:RSI:5     → bars=5
                        if (msg.StartsWith("INDICATOR:", StringComparison.OrdinalIgnoreCase))
                        {
                            string rest = msg.Substring("INDICATOR:".Length).Trim();
                            int bars = 1;
                            string indName = rest;
                            int colonIdx = rest.LastIndexOf(':');
                            if (colonIdx > 0)
                            {
                                string tail = rest.Substring(colonIdx + 1).Trim();
                                if (int.TryParse(tail, out int parsedBars) && parsedBars > 0)
                                {
                                    bars = parsedBars;
                                    indName = rest.Substring(0, colonIdx).Trim();
                                }
                            }
                            await SendMessageAsync(ws, GetIndicatorValue(indName, bars));
                            continue;
                        }

                        // ── Bars: current ──
                        if (msg.Equals("current", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendCurrentBar(ws).ConfigureAwait(false);
                            continue;
                        }

                        // ── Bars: recent N ──
                        if (int.TryParse(msg, out int n) && n > 0)
                        {
                            await SendRecentBars(ws, n).ConfigureAwait(false);
                            continue;
                        }
                    }
                    else if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token).ConfigureAwait(false);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"ReceiveLoop error: {ex.Message}");
            }
        }

        private async Task SendMessageAsync(WebSocket ws, string m)
        {
            if (ws.State != WebSocketState.Open) return;
            try
            {
                await ws.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(m)),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Print($"Error sending message: {ex.Message}");
            }
        }

        private async Task SendCurrentBar(WebSocket ws)
        {
            // Retorna o último bar FECHADO de chartBars[0] (populado em OnBarUpdate).
            // Pra preço live dentro do bar em formação, cliente usa nt_get_quote
            // (alimentado por OnMarketData per-tick, sempre em tempo real).
            BarDataSnapshot snap = null;
            lock (barsLock)
            {
                if (chartBars.Count > 0)
                {
                    var b = chartBars[0];
                    snap = new BarDataSnapshot { Timestamp = b.Timestamp, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, Volume = b.Volume };
                }
            }
            if (snap == null)
            {
                await SendMessageAsync(ws, "ERROR:Buffer_Empty").ConfigureAwait(false);
                return;
            }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var d = $"{snap.Timestamp:yyyyMMdd HHmmss} {snap.Open.ToString("F2", inv)} {snap.High.ToString("F2", inv)} {snap.Low.ToString("F2", inv)} {snap.Close.ToString("F2", inv)} {snap.Volume}";
            await SendMessageAsync(ws, "CURRENT;" + d).ConfigureAwait(false);
        }

        private class BarDataSnapshot
        {
            public DateTime Timestamp { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
            public long Volume { get; set; }
        }

        private async Task SendRecentBars(WebSocket ws, int cnt)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            List<BarData> toSend;
            lock (barsLock) toSend = chartBars.Take(cnt).ToList();

            await SendMessageAsync(ws, $"BEGIN:{toSend.Count}").ConfigureAwait(false);
            for (int i = 0; i < toSend.Count; i++)
            {
                var b = toSend[i];
                string line = $"BAR:{i}:{b.Timestamp:yyyyMMdd HHmmss} {b.Open.ToString("F2", inv)} {b.High.ToString("F2", inv)} {b.Low.ToString("F2", inv)} {b.Close.ToString("F2", inv)} {b.Volume}";
                await SendMessageAsync(ws, line).ConfigureAwait(false);
                if (i % 10 == 0) await Task.Delay(5).ConfigureAwait(false);
            }
            await SendMessageAsync(ws, "END").ConfigureAwait(false);
            Print($"[{Name}] Enviadas {toSend.Count} barras");
        }

        private void StopWebSocketServer()
        {
            try
            {
                httpListener?.Stop();
                httpListener?.Close();
            }
            catch (Exception ex)
            {
                Print($"Error stopping server: {ex.Message}");
            }
            lock (clientsLock)
            {
                foreach (var ws in clients)
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Shutdown", CancellationToken.None).Wait(500);
                        ws.Dispose();
                    }
                    catch { }
                }
                clients.Clear();
            }
        }

        public void Dispose()
        {
            StopWebSocketServer();
            cts?.Dispose();
        }
    }
}

