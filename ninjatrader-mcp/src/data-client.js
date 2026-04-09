import WebSocket from 'ws';

/**
 * WebSocket client for NinjaMCPServer (data stream, default port 8000).
 *
 * Behavior:
 * - Does not throw on initial connection failure; logs to stderr and retries.
 * - Reconnects automatically on 'close' after 2s.
 * - Request/response matched by EXPECTED RESPONSE PREFIX (not FIFO queue).
 *   Why: after a timeout the stale response still arrives later and would
 *   be delivered to the next pending resolver, causing a cascading queue
 *   desync ("bars response delivered to get_status caller" bug).
 * - Periodic ping (30s) to detect half-closed connections and force reconnect.
 */
export class DataClient {
  constructor(url) {
    this.url = url;
    this.ws = null;
    this.connected = false;
    // Map of expectedPrefix → resolver. Each request registers its own matcher.
    this.pendingRequests = [];
    this.reconnectTimer = null;
    this.pingTimer = null;
    this.lastPingRecvAt = 0;
    this.barAccumulator = null;
  }

  connect() {
    try {
      this.ws = new WebSocket(this.url);
    } catch (err) {
      console.error(`[DataClient] Failed to create WebSocket: ${err.message}`);
      this.scheduleReconnect();
      return;
    }

    this.ws.on('open', () => {
      this.connected = true;
      this.lastPingRecvAt = Date.now();
      console.error(`[DataClient] Connected to ${this.url}`);
      this.startPingLoop();
    });

    this.ws.on('message', (data) => {
      this.handleMessage(data.toString());
    });

    this.ws.on('error', (err) => {
      console.error(`[DataClient] Error: ${err.message}`);
    });

    this.ws.on('close', () => {
      this.connected = false;
      this.stopPingLoop();
      // Fail all pending requests on disconnect
      for (const req of this.pendingRequests) {
        try { req.reject(new Error('Disconnected')); } catch (_) {}
      }
      this.pendingRequests = [];
      this.barAccumulator = null;
      console.error('[DataClient] Disconnected, reconnecting in 2s...');
      this.scheduleReconnect();
    });
  }

  startPingLoop() {
    this.stopPingLoop();
    this.pingTimer = setInterval(() => {
      // If we haven't received a pong-equivalent (any message) in 60s, force reconnect
      if (Date.now() - this.lastPingRecvAt > 60000) {
        console.error('[DataClient] No activity for 60s — force reconnecting');
        try { this.ws.terminate(); } catch (_) {}
        return;
      }
      // Send a lightweight ping
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

    // Ignore async broadcast messages — they are not responses to commands.
    if (msg.startsWith('UPDATE;')) return;
    if (msg.startsWith('CONNECTION_OK')) return;

    // Multi-message BAR responses: BEGIN:N → BAR:0..N-1 → END
    if (msg.startsWith('BEGIN:')) {
      this.barAccumulator = [msg];
      return;
    }
    if (this.barAccumulator) {
      this.barAccumulator.push(msg);
      if (msg === 'END' || msg.startsWith('END')) {
        const combined = this.barAccumulator.join('\n');
        this.barAccumulator = null;
        this.deliver(combined);
      }
      return;
    }

    // All responses route through matcher-based dispatch (including PONG)
    this.deliver(msg);
  }

  // Deliver to the first pending request whose matcher accepts this message.
  deliver(msg) {
    for (let i = 0; i < this.pendingRequests.length; i++) {
      const req = this.pendingRequests[i];
      if (req.matcher(msg)) {
        this.pendingRequests.splice(i, 1);
        req.resolve(msg);
        return;
      }
    }
    // Orphan message (timed-out request, or no matcher) — drop silently
  }

  isConnected() {
    return this.connected && this.ws && this.ws.readyState === WebSocket.OPEN;
  }

  // Build a matcher function that recognizes the expected response for a given command
  buildMatcher(command) {
    const cmd = command.trim();

    if (cmd === 'PING_HEALTH_CHECK') {
      return (msg) => msg === 'PONG_HEALTH_CHECK';
    }
    if (cmd === 'QUOTE') {
      return (msg) => msg.startsWith('QUOTE:');
    }
    if (cmd === 'INDICATORS' || cmd.startsWith('INDICATORS:')) {
      return (msg) => msg.startsWith('INDICATORS:');
    }
    if (cmd.startsWith('INDICATOR:')) {
      // Server responds with INDICATOR:{name}:{json}
      return (msg) => msg.startsWith('INDICATOR:');
    }
    if (cmd.startsWith('TIMESALES:')) {
      return (msg) => msg.startsWith('TIMESALES:');
    }
    if (cmd === 'current') {
      return (msg) => msg.startsWith('CURRENT;');
    }
    // Numeric = get N bars; response is multi-line BEGIN/BAR/END (accumulated)
    if (/^\d+$/.test(cmd)) {
      return (msg) => msg.startsWith('BEGIN:');
    }
    // Fallback: accept any non-broadcast message
    return () => true;
  }

  async sendCommand(command, timeout = 5000) {
    if (!this.isConnected()) {
      throw new Error(`DataClient not connected to ${this.url}`);
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
