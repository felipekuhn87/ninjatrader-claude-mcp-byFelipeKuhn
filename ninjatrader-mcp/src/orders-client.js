import WebSocket from 'ws';

/**
 * WebSocket client for NinjaMCPExecute (order execution, default port 8002).
 *
 * Same semantics as DataClient:
 * - Matcher-based dispatch (not FIFO queue) to avoid cascading desync when
 *   async broadcasts arrive (ORDER_UPDATE:, POSITION_CLOSED:, EXITED_ALL_POSITIONS,
 *   STOPLOSS_SET:, PROFIT_TARGET_SET:, etc — sent by strategy without request).
 * - Periodic ping (30s) + 60s inactivity force-reconnect.
 * - Reconnects on close after 2s.
 */
export class OrdersClient {
  constructor(url) {
    this.url = url;
    this.ws = null;
    this.connected = false;
    this.pendingRequests = [];
    this.reconnectTimer = null;
    this.pingTimer = null;
    this.lastPingRecvAt = 0;
  }

  connect() {
    try {
      this.ws = new WebSocket(this.url);
    } catch (err) {
      console.error(`[OrdersClient] Failed to create WebSocket: ${err.message}`);
      this.scheduleReconnect();
      return;
    }

    this.ws.on('open', () => {
      this.connected = true;
      this.lastPingRecvAt = Date.now();
      console.error(`[OrdersClient] Connected to ${this.url}`);
      this.startPingLoop();
    });

    this.ws.on('message', (data) => {
      this.handleMessage(data.toString());
    });

    this.ws.on('error', (err) => {
      console.error(`[OrdersClient] Error: ${err.message}`);
    });

    this.ws.on('close', () => {
      this.connected = false;
      this.stopPingLoop();
      for (const req of this.pendingRequests) {
        try { req.reject(new Error('Disconnected')); } catch (_) {}
      }
      this.pendingRequests = [];
      console.error('[OrdersClient] Disconnected, reconnecting in 2s...');
      this.scheduleReconnect();
    });
  }

  startPingLoop() {
    this.stopPingLoop();
    this.pingTimer = setInterval(() => {
      if (Date.now() - this.lastPingRecvAt > 60000) {
        console.error('[OrdersClient] No activity for 60s — force reconnecting');
        try { this.ws.terminate(); } catch (_) {}
        return;
      }
      try {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
          this.ws.send('PING_HEALTH_CHECK');
        }
      } catch (_) {}
    }, 30000);
  }

  stopPingLoop() {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
  }

  scheduleReconnect() {
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, 2000);
  }

  handleMessage(msg) {
    this.lastPingRecvAt = Date.now();

    // Ignore async broadcasts from the strategy that are NOT responses to commands.
    // These are fire-and-forget notifications sent by the strategy on state changes.
    if (msg.startsWith('CONNECTION_OK')) return;
    if (msg.startsWith('ORDER_UPDATE:')) return;       // fill notifications
    if (msg.startsWith('POSITION_CLOSED:')) return;     // trade close narrative
    if (msg === 'EXITED_ALL_POSITIONS') return;         // flat confirmation
    if (msg.startsWith('STOP_DIVERGENCE')) return;      // server diagnostic

    this.deliver(msg);
  }

  deliver(msg) {
    for (let i = 0; i < this.pendingRequests.length; i++) {
      const req = this.pendingRequests[i];
      if (req.matcher(msg)) {
        this.pendingRequests.splice(i, 1);
        req.resolve(msg);
        return;
      }
    }
    // Orphan response (timed-out or no matcher) — drop silently
  }

  isConnected() {
    return this.connected && this.ws && this.ws.readyState === WebSocket.OPEN;
  }

  buildMatcher(command) {
    const cmd = command.trim();

    if (cmd === 'PING_HEALTH_CHECK') {
      return (msg) => msg === 'PONG_HEALTH_CHECK';
    }
    if (cmd === 'GetStatus') {
      return (msg) => msg.startsWith('STATUS:');
    }
    if (cmd.startsWith('EnterLongOrder') || cmd.startsWith('EnterShortOrder')) {
      // Server responds with ORDER_SENT:Long;... or ORDER_SENT:Short;...
      // (other async messages like STOPLOSS_SET: may arrive later — those are
      // ignored as broadcasts in handleMessage, OR matched by separate STOPLOSS cmd)
      return (msg) => msg.startsWith('ORDER_SENT:') || msg.startsWith('ERROR:');
    }
    if (cmd.startsWith('STOPLOSS:')) {
      return (msg) => msg.startsWith('STOPLOSS_SET:') || msg.startsWith('ERROR:');
    }
    if (cmd.startsWith('PROFIT:')) {
      return (msg) => msg.startsWith('PROFIT_TARGET_SET:') || msg.startsWith('ERROR:');
    }
    if (cmd.startsWith('BREAKEVEN')) {
      return (msg) => msg.startsWith('BREAKEVEN_SET:') || msg.startsWith('ERROR:');
    }
    if (cmd === 'CancelAllStops') {
      return (msg) => msg === 'ALL_STOPS_CANCELLED' || msg.startsWith('ERROR:');
    }
    if (cmd === 'CANCEL_PROFIT') {
      return (msg) => msg === 'PROFIT_CANCELLED' || msg.startsWith('ERROR:');
    }
    if (cmd === 'ExitOrders') {
      // ExitOrders triggers ExitLong/ExitShort. The strategy doesn't send a
      // direct ack — it sends POSITION_CLOSED: + EXITED_ALL_POSITIONS later.
      // We return a synthetic ack by accepting nothing and relying on the
      // tool's timeout to complete. Alternatively, accept any STATUS: msg.
      // Safer: just return immediately — the caller should re-query status.
      return () => true; // accept anything; caller will re-query status
    }
    // Fallback
    return () => true;
  }

  async sendCommand(command, timeout = 5000) {
    if (!this.isConnected()) {
      throw new Error(`OrdersClient not connected to ${this.url}`);
    }

    return new Promise((resolve, reject) => {
      const matcher = this.buildMatcher(command);
      const req = {
        command,
        matcher,
        resolve: (msg) => {
          clearTimeout(timer);
          resolve(msg);
        },
        reject: (err) => {
          clearTimeout(timer);
          reject(err);
        },
      };
      this.pendingRequests.push(req);

      const timer = setTimeout(() => {
        const idx = this.pendingRequests.indexOf(req);
        if (idx !== -1) this.pendingRequests.splice(idx, 1);
        reject(new Error(`Timeout waiting for response to: ${command}`));
      }, timeout);

      try {
        this.ws.send(command);
      } catch (err) {
        const idx = this.pendingRequests.indexOf(req);
        if (idx !== -1) this.pendingRequests.splice(idx, 1);
        clearTimeout(timer);
        reject(err);
      }
    });
  }
}
