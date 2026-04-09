#!/usr/bin/env node
/**
 * Standalone connection test for ninjatrader-mcp.
 *
 * Connects to both WebSocket endpoints, sends PING_HEALTH_CHECK (data)
 * and GetStatus (orders), prints results and exits.
 *
 * Usage:
 *   node scripts/test-connection.js
 *   NT_DATA_WS=ws://host:8000/ws NT_ORDERS_WS=ws://host:8002 node scripts/test-connection.js
 */
import WebSocket from 'ws';

const DATA_WS = process.env.NT_DATA_WS || 'ws://localhost:8000/ws';
const ORDERS_WS = process.env.NT_ORDERS_WS || 'ws://localhost:8002';

function testEndpoint(url, command, timeout = 3000) {
  return new Promise((resolve) => {
    const result = { url, connected: false, response: null, error: null };
    let ws;
    try {
      ws = new WebSocket(url);
    } catch (err) {
      result.error = err.message;
      return resolve(result);
    }

    const timer = setTimeout(() => {
      result.error = `Timeout after ${timeout}ms`;
      try { ws.close(); } catch {}
      resolve(result);
    }, timeout);

    ws.on('open', () => {
      result.connected = true;
      try {
        ws.send(command);
      } catch (err) {
        result.error = `Send failed: ${err.message}`;
        clearTimeout(timer);
        try { ws.close(); } catch {}
        resolve(result);
      }
    });

    ws.on('message', (data) => {
      result.response = data.toString();
      clearTimeout(timer);
      try { ws.close(); } catch {}
      resolve(result);
    });

    ws.on('error', (err) => {
      result.error = err.message;
      clearTimeout(timer);
      resolve(result);
    });

    ws.on('close', () => {
      if (!result.response && !result.error) {
        result.error = 'Closed without response';
        clearTimeout(timer);
        resolve(result);
      }
    });
  });
}

(async () => {
  console.log('NinjaTrader MCP — connection test\n');

  console.log(`[1/2] Testing DATA endpoint: ${DATA_WS}`);
  const data = await testEndpoint(DATA_WS, 'PING_HEALTH_CHECK');
  if (data.connected && data.response && !data.error) {
    console.log(`      OK — response: ${data.response}`);
  } else {
    console.log(`      FAIL — connected=${data.connected} error=${data.error || 'none'}`);
  }

  console.log(`\n[2/2] Testing ORDERS endpoint: ${ORDERS_WS}`);
  const orders = await testEndpoint(ORDERS_WS, 'GetStatus');
  if (orders.connected && orders.response && !orders.error) {
    console.log(`      OK — response: ${orders.response}`);
  } else {
    console.log(`      FAIL — connected=${orders.connected} error=${orders.error || 'none'}`);
  }

  const bothOk = data.connected && !data.error && orders.connected && !orders.error;
  console.log(`\n${bothOk ? 'Both connections OK' : 'One or more connections failed'}`);
  process.exit(bothOk ? 0 : 1);
})();
