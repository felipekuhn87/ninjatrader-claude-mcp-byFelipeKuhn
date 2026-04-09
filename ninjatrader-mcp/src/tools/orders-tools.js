import { parseStatusResponse } from '../parsers/status-parser.js';

export const ordersTools = [
  {
    name: 'nt_enter_long',
    description: 'Enter a LONG position with optional atomic stop loss and take profit (placed as a single atomic order).',
    inputSchema: {
      type: 'object',
      properties: {
        qty: { type: 'number', description: 'Number of contracts', default: 1 },
        sl: { type: 'number', description: 'Stop Loss price (optional)' },
        tp: { type: 'number', description: 'Take Profit price (optional)' },
      },
      required: ['qty'],
    },
  },
  {
    name: 'nt_enter_short',
    description: 'Enter a SHORT position with optional atomic stop loss and take profit.',
    inputSchema: {
      type: 'object',
      properties: {
        qty: { type: 'number', description: 'Number of contracts', default: 1 },
        sl: { type: 'number', description: 'Stop Loss price (optional)' },
        tp: { type: 'number', description: 'Take Profit price (optional)' },
      },
      required: ['qty'],
    },
  },
  {
    name: 'nt_set_stoploss',
    description: 'Modify the stop loss for the active position. Validates stop is on the correct side of entry.',
    inputSchema: {
      type: 'object',
      properties: {
        price: { type: 'number', description: 'New stop loss price' },
      },
      required: ['price'],
    },
  },
  {
    name: 'nt_set_profit',
    description: 'Modify the take profit target for the active position.',
    inputSchema: {
      type: 'object',
      properties: {
        price: { type: 'number', description: 'New take profit price' },
      },
      required: ['price'],
    },
  },
  {
    name: 'nt_breakeven',
    description: 'Move stop loss to breakeven (entry price). Optionally provide an explicit offset price.',
    inputSchema: {
      type: 'object',
      properties: {
        offset: { type: 'number', description: 'Optional explicit breakeven price (default = entry price)' },
      },
    },
  },
  {
    name: 'nt_cancel_all_stops',
    description: 'Cancel all active stop loss orders for the instrument.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_cancel_profit',
    description: 'Cancel the active take profit target.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_exit_all',
    description: 'Flatten position immediately: close all contracts and cancel pending stops/targets.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_get_status',
    description: 'Get full strategy status: position state, entry, stop, profit, current price and stats.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'nt_get_position',
    description: 'Get current position data (alias of nt_get_status but returning only position-related fields).',
    inputSchema: { type: 'object', properties: {} },
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

function interpret(response) {
  if (!response) return { success: false, response };
  const upper = response.toUpperCase();
  const isErr = upper.startsWith('ERROR') || upper.startsWith('REJECTED');
  return { success: !isErr, response };
}

export async function handleOrdersTool(name, args = {}, client) {
  try {
    if (!client.isConnected()) {
      return err('Orders client not connected to NinjaTrader (port 8002). Ensure NinjaMCPExecute is enabled on a chart.');
    }

    switch (name) {
      case 'nt_enter_long': {
        const qty = args.qty ?? 1;
        let cmd = `EnterLongOrder:${qty}`;
        if (args.sl !== undefined && args.sl !== null) cmd += `:SL=${Number(args.sl).toFixed(2)}`;
        if (args.tp !== undefined && args.tp !== null) cmd += `:TP=${Number(args.tp).toFixed(2)}`;
        const response = await client.sendCommand(cmd);
        return ok({ ...interpret(response), signal: 'Long', qty });
      }

      case 'nt_enter_short': {
        const qty = args.qty ?? 1;
        let cmd = `EnterShortOrder:${qty}`;
        if (args.sl !== undefined && args.sl !== null) cmd += `:SL=${Number(args.sl).toFixed(2)}`;
        if (args.tp !== undefined && args.tp !== null) cmd += `:TP=${Number(args.tp).toFixed(2)}`;
        const response = await client.sendCommand(cmd);
        return ok({ ...interpret(response), signal: 'Short', qty });
      }

      case 'nt_set_stoploss': {
        if (args.price === undefined || args.price === null) return err('Missing required parameter: price');
        const cmd = `STOPLOSS:${Number(args.price).toFixed(2)}`;
        const response = await client.sendCommand(cmd);
        return ok({ ...interpret(response), stop_price: Number(args.price) });
      }

      case 'nt_set_profit': {
        if (args.price === undefined || args.price === null) return err('Missing required parameter: price');
        const cmd = `PROFIT:${Number(args.price).toFixed(2)}`;
        const response = await client.sendCommand(cmd);
        return ok({ ...interpret(response), profit_price: Number(args.price) });
      }

      case 'nt_breakeven': {
        let cmd = 'BREAKEVEN';
        if (args.offset !== undefined && args.offset !== null) {
          cmd = `BREAKEVEN:${Number(args.offset).toFixed(2)}`;
        }
        const response = await client.sendCommand(cmd);
        return ok({
          ...interpret(response),
          breakeven_price: args.offset !== undefined ? Number(args.offset) : undefined,
        });
      }

      case 'nt_cancel_all_stops': {
        const response = await client.sendCommand('CancelAllStops');
        return ok(interpret(response));
      }

      case 'nt_cancel_profit': {
        const response = await client.sendCommand('CANCEL_PROFIT');
        return ok(interpret(response));
      }

      case 'nt_exit_all': {
        const response = await client.sendCommand('ExitOrders');
        return ok(interpret(response));
      }

      case 'nt_get_status': {
        const response = await client.sendCommand('GetStatus');
        const parsed = parseStatusResponse(response);
        return ok({ success: !parsed.error, status: parsed });
      }

      case 'nt_get_position': {
        const response = await client.sendCommand('GetStatus');
        const parsed = parseStatusResponse(response);
        // Filter to position-only fields
        const positionKeys = [
          'in_position',
          'position_type',
          'qty',
          'quantity',
          'entry_price',
          'stop_price',
          'profit_price',
          'current_price',
          'unrealized_pnl',
        ];
        const position = {};
        for (const k of positionKeys) {
          if (k in parsed) position[k] = parsed[k];
        }
        return ok({ success: !parsed.error, position });
      }

      default:
        return err(`Unknown orders tool: ${name}`);
    }
  } catch (e) {
    return err(`${name} failed: ${e.message}`);
  }
}
