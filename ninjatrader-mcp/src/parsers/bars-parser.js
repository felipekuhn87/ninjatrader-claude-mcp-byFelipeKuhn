/**
 * Parses the bars response from NinjaMCPServer.
 *
 * Expected format:
 *   BEGIN:N
 *   BAR:0:yyyyMMdd HHmmss O H L C V
 *   BAR:1:...
 *   ...
 *   END
 *
 * Returns { bars: [...] } or { summary: {...} } depending on summary flag.
 */
export function parseBarsResponse(raw, requestedCount, summary = false) {
  if (!raw || typeof raw !== 'string') {
    return { bars: [], error: 'empty response' };
  }

  if (raw.startsWith('ERROR')) {
    return { bars: [], error: raw };
  }

  const lines = raw.split(/\r?\n/);
  const bars = [];

  for (const line of lines) {
    if (!line.startsWith('BAR:')) continue;

    // BAR:{idx}:{yyyyMMdd} {HHmmss} {O} {H} {L} {C} {V}
    const rest = line.substring(4); // strip "BAR:"
    const firstColon = rest.indexOf(':');
    if (firstColon === -1) continue;

    const idxStr = rest.substring(0, firstColon);
    const payload = rest.substring(firstColon + 1);
    const parts = payload.split(/\s+/);

    if (parts.length < 7) continue;

    const [datePart, timePart, o, h, l, c, v] = parts;

    // Format yyyyMMdd HHmmss -> ISO-ish string
    let timeIso = `${datePart} ${timePart}`;
    if (datePart.length === 8 && timePart.length === 6) {
      const yyyy = datePart.substring(0, 4);
      const mm = datePart.substring(4, 6);
      const dd = datePart.substring(6, 8);
      const HH = timePart.substring(0, 2);
      const MM = timePart.substring(2, 4);
      const SS = timePart.substring(4, 6);
      timeIso = `${yyyy}-${mm}-${dd}T${HH}:${MM}:${SS}`;
    }

    bars.push({
      idx: parseInt(idxStr, 10),
      time: timeIso,
      o: parseFloat(o),
      h: parseFloat(h),
      l: parseFloat(l),
      c: parseFloat(c),
      v: parseInt(v, 10),
    });
  }

  if (summary) {
    return { summary: computeSummary(bars), count: bars.length };
  }

  return { bars, count: bars.length };
}

export function parseCurrentBar(raw) {
  if (!raw || typeof raw !== 'string') return { error: 'empty response' };
  if (raw.startsWith('ERROR')) return { error: raw };

  // Expected similar format: BAR:0:yyyyMMdd HHmmss O H L C V  or raw "current" line
  const line = raw.split(/\r?\n/).find((l) => l.startsWith('BAR:')) || raw;
  if (!line.startsWith('BAR:')) {
    return { raw };
  }

  const rest = line.substring(4);
  const firstColon = rest.indexOf(':');
  const payload = firstColon === -1 ? rest : rest.substring(firstColon + 1);
  const parts = payload.split(/\s+/);
  if (parts.length < 7) return { raw };

  const [datePart, timePart, o, h, l, c, v] = parts;
  let timeIso = `${datePart} ${timePart}`;
  if (datePart.length === 8 && timePart.length === 6) {
    timeIso = `${datePart.substring(0, 4)}-${datePart.substring(4, 6)}-${datePart.substring(6, 8)}T${timePart.substring(0, 2)}:${timePart.substring(2, 4)}:${timePart.substring(4, 6)}`;
  }

  return {
    time: timeIso,
    o: parseFloat(o),
    h: parseFloat(h),
    l: parseFloat(l),
    c: parseFloat(c),
    v: parseInt(v, 10),
  };
}

function computeSummary(bars) {
  if (bars.length === 0) {
    return { count: 0 };
  }

  let high = -Infinity;
  let low = Infinity;
  let volume = 0;

  for (const b of bars) {
    if (b.h > high) high = b.h;
    if (b.l < low) low = b.l;
    volume += b.v;
  }

  const open = bars[0].o;
  const close = bars[bars.length - 1].c;
  const range = high - low;

  return {
    count: bars.length,
    first_time: bars[0].time,
    last_time: bars[bars.length - 1].time,
    open,
    high,
    low,
    close,
    volume,
    range: Number(range.toFixed(4)),
    change: Number((close - open).toFixed(4)),
  };
}
