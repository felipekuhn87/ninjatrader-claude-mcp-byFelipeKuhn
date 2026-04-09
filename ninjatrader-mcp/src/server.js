#!/usr/bin/env node
/**
 * NinjaTrader MCP Server
 *
 * Entry point: exposes 19 tools (9 data + 10 orders) that bridge Claude Code
 * to NinjaTrader 8 via two WebSocket endpoints:
 *   - Data stream:     ws://localhost:8000/ws  (NinjaMCPServer.cs indicator)
 *   - Order execution: ws://localhost:8002     (NinjaMCPExecute.cs strategy)
 *
 * The server starts even if NinjaTrader is offline — both clients reconnect
 * automatically. Tool calls will return {success:false,error:...} until the
 * underlying WebSocket is connected.
 */
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';

import { DataClient } from './data-client.js';
import { OrdersClient } from './orders-client.js';
import { dataTools, handleDataTool } from './tools/data-tools.js';
import { ordersTools, handleOrdersTool } from './tools/orders-tools.js';

const DATA_WS = process.env.NT_DATA_WS || 'ws://localhost:8000/ws';
const ORDERS_WS = process.env.NT_ORDERS_WS || 'ws://localhost:8002';

console.error(`[ninjatrader-mcp] Starting. DATA=${DATA_WS} ORDERS=${ORDERS_WS}`);

const dataClient = new DataClient(DATA_WS);
const ordersClient = new OrdersClient(ORDERS_WS);

// Fire and forget — never await connect. If NT is offline, reconnection handles it.
dataClient.connect();
ordersClient.connect();

const DATA_TOOL_NAMES = new Set(dataTools.map((t) => t.name));
const ORDERS_TOOL_NAMES = new Set(ordersTools.map((t) => t.name));

const server = new Server(
  { name: 'ninjatrader-mcp', version: '1.0.0' },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [...dataTools, ...ordersTools],
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  const { name, arguments: args } = request.params;

  if (DATA_TOOL_NAMES.has(name)) {
    return handleDataTool(name, args || {}, dataClient);
  }

  if (ORDERS_TOOL_NAMES.has(name)) {
    return handleOrdersTool(name, args || {}, ordersClient);
  }

  return {
    content: [
      {
        type: 'text',
        text: JSON.stringify({ success: false, error: `Unknown tool: ${name}` }),
      },
    ],
    isError: true,
  };
});

const transport = new StdioServerTransport();
await server.connect(transport);

console.error('[ninjatrader-mcp] MCP server ready on stdio transport.');
