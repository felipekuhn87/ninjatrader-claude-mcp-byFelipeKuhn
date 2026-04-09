import {
  parseBarsResponse,
  parseCurrentBar,
} from '../parsers/bars-parser.js';
import {
  parseQuoteResponse,
  parseTimeSalesResponse,
} from '../parsers/dom-parser.js';
import {
  parseIndicatorsResponse,
  parseIndicatorValueResponse,
} from '../parsers/indicators-parser.js';

export const dataTools = [
  {
    name: 'nt_health_check',
    description: 'Check WebSocket connection to NinjaTrader data server (port 8000). Returns connection status.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_get_bars',
    description: 'Get OHLCV bars from NinjaTrader chart. Use summary=true for compact high/low/open/close/volume/range stats instead of full bar array.',
    inputSchema: {
      type: 'object',
      properties: {
        count: { type: 'number', description: 'Number of bars to retrieve (max 1000)', default: 100 },
        summary: { type: 'boolean', description: 'Return summary stats only', default: false },
      },
    },
  },
  {
    name: 'nt_get_current_bar',
    description: 'Get the current (latest, in-progress) OHLCV bar from the chart.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_get_quote',
    description: 'Get current best bid/ask/last/volume quote for the instrument.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_get_indicators',
    description: 'Discover all indicators on the chart. Returns list with name, displayName and plot values. With bars=1 (default) each plot has "value" (scalar). With bars>1 each plot has "values" (array, index 0 = most recent) — use this to detect crossovers, divergences, momentum shifts.',
    inputSchema: {
      type: 'object',
      properties: {
        bars: { type: 'number', description: 'Number of recent bars to return per plot. 1 = current value only (cheap). 5-20 = enough for crossover/divergence detection. Max 500.', default: 1 },
      },
    },
  },
  {
    name: 'nt_get_indicator_value',
    description: 'Get plot values of a specific indicator by name (matches name or displayName). With bars=1 (default) returns scalar per plot. With bars>1 returns array per plot (index 0 = most recent).',
    inputSchema: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Indicator name (e.g. "RSI", "MACD", "EMA")' },
        bars: { type: 'number', description: 'Number of recent bars per plot. Default 1. Max 500.', default: 1 },
      },
      required: ['name'],
    },
  },
  {
    name: 'nt_get_time_sales',
    description: 'Get recent Time & Sales ticks (Last/Bid/Ask prints) from a rolling buffer.',
    inputSchema: {
      type: 'object',
      properties: {
        count: { type: 'number', description: 'Number of most recent ticks to retrieve', default: 50 },
      },
    },
  },
];

function ok(obj) {
  return { content: [{ type: 'text', text: JSON.stringify(obj) }] };
}

function err(msg, extra = {}) {
  return {
    content: [{ type: 'text', text: JSON.stringify({ success: false, error: msg, ...extra }) }],
    isError: true,
  };
}

export async function handleDataTool(name, args = {}, client) {
  try {
    if (!client.isConnected() && name !== 'nt_health_check') {
      return err(`Data client not connected to NinjaTrader (port 8000). Ensure NinjaMCPServer indicator is active on a chart.`);
    }

    switch (name) {
      case 'nt_health_check': {
        if (!client.isConnected()) {
          return ok({ success: false, connected: false, message: 'Not connected to ws://localhost:8000/ws' });
        }
        try {
          const response = await client.sendCommand('PING_HEALTH_CHECK', 2000);
          return ok({
            success: response.includes('PONG') || response.includes('HEALTH'),
            connected: true,
            response,
          });
        } catch (e) {
          return ok({ success: false, connected: true, error: e.message });
        }
      }

      case 'nt_get_bars': {
        const count = args.count || 100;
        const summary = !!args.summary;
        const response = await client.sendCommand(`${count}`);
        const parsed = parseBarsResponse(response, count, summary);
        return ok({ success: true, ...parsed });
      }

      case 'nt_get_current_bar': {
        const response = await client.sendCommand('current');
        const parsed = parseCurrentBar(response);
        return ok({ success: !parsed.error, ...parsed });
      }

      case 'nt_get_quote': {
        const response = await client.sendCommand('QUOTE');
        const parsed = parseQuoteResponse(response);
        return ok({ success: !parsed.error, ...parsed });
      }

      case 'nt_get_indicators': {
        const bars = Math.max(1, Math.min(500, args.bars || 1));
        const cmd = bars > 1 ? `INDICATORS:${bars}` : 'INDICATORS';
        const response = await client.sendCommand(cmd);
        const parsed = parseIndicatorsResponse(response);
        return ok({ success: !parsed.error, bars, ...parsed });
      }

      case 'nt_get_indicator_value': {
        if (!args.name) return err('Missing required parameter: name');
        const bars = Math.max(1, Math.min(500, args.bars || 1));
        const cmd = bars > 1 ? `INDICATOR:${args.name}:${bars}` : `INDICATOR:${args.name}`;
        const response = await client.sendCommand(cmd);
        const parsed = parseIndicatorValueResponse(response);
        return ok({ success: !parsed.error, bars, ...parsed });
      }

      case 'nt_get_time_sales': {
        const count = args.count || 50;
        const response = await client.sendCommand(`TIMESALES:${count}`);
        const parsed = parseTimeSalesResponse(response);
        return ok({ success: !parsed.error, ...parsed });
      }

      default:
        return err(`Unknown data tool: ${name}`);
    }
  } catch (e) {
    return err(`${name} failed: ${e.message}`);
  }
}
