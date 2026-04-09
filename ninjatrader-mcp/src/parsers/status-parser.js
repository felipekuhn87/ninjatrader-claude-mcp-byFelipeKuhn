/**
 * Parses status response from NinjaMCPExecute.
 *
 * Expected format:
 *   STATUS:InPosition=true;PositionType=Long;Qty=2;EntryPrice=24140.00;StopPrice=24128.00;ProfitPrice=24165.00;CurrentPrice=24145.00;...
 *
 * Splits by ';' then by '='. Normalizes booleans and numbers.
 */
export function parseStatusResponse(raw) {
  if (!raw || typeof raw !== 'string') return { error: 'empty' };
  if (raw.startsWith('ERROR')) return { error: raw };

  const prefix = 'STATUS:';
  const body = raw.startsWith(prefix) ? raw.substring(prefix.length) : raw;

  const result = {};
  const pairs = body.split(';');
  for (const pair of pairs) {
    if (!pair || !pair.includes('=')) continue;
    const [rawKey, ...rest] = pair.split('=');
    const key = rawKey.trim();
    const value = rest.join('=').trim();
    if (!key) continue;
    result[normalizeKey(key)] = normalizeValue(value);
  }

  // Convenience flag
  if ('in_position' in result) {
    result.in_position = !!result.in_position;
  }

  return result;
}

function normalizeKey(key) {
  // InPosition -> in_position
  return key
    .replace(/([a-z])([A-Z])/g, '$1_$2')
    .replace(/([A-Z])([A-Z][a-z])/g, '$1_$2')
    .toLowerCase();
}

function normalizeValue(value) {
  if (value === '' || value === null || value === undefined) return null;
  const lower = value.toLowerCase();
  if (lower === 'true') return true;
  if (lower === 'false') return false;
  if (lower === 'none' || lower === 'null') return null;

  // Try number
  if (/^-?\d+(\.\d+)?$/.test(value)) {
    const n = Number(value);
    if (!Number.isNaN(n)) return n;
  }
  return value;
}
