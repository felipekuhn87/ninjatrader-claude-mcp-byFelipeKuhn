/**
 * Parses Quote response. Expected format:
 * QUOTE:{"bid":..,"ask":..,"last":..,"volume":..}
 */
export function parseQuoteResponse(raw) {
  if (!raw || typeof raw !== 'string') return { error: 'empty' };
  if (raw.startsWith('ERROR')) return { error: raw };

  const prefix = 'QUOTE:';
  const jsonStr = raw.startsWith(prefix) ? raw.substring(prefix.length) : raw;

  try {
    return JSON.parse(jsonStr);
  } catch (err) {
    return { error: `QUOTE parse failed: ${err.message}`, raw };
  }
}

/**
 * Parses Time & Sales response. Expected format:
 * TIMESALES:[{"time":..,"type":..,"price":..,"volume":..}, ...]
 */
export function parseTimeSalesResponse(raw) {
  if (!raw || typeof raw !== 'string') return { ticks: [], error: 'empty' };
  if (raw.startsWith('ERROR')) return { ticks: [], error: raw };

  const prefix = 'TIMESALES:';
  const jsonStr = raw.startsWith(prefix) ? raw.substring(prefix.length) : raw;

  try {
    const arr = JSON.parse(jsonStr);
    return { ticks: Array.isArray(arr) ? arr : [], count: Array.isArray(arr) ? arr.length : 0 };
  } catch (err) {
    return { ticks: [], error: `TIMESALES parse failed: ${err.message}`, raw };
  }
}
