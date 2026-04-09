/**
 * Parses indicators response from NinjaMCPServer.
 *
 * Expected format for nt_get_indicators:
 *   INDICATORS:[{"name":..,"displayName":..,"plots":[{"name":..,"value":..}]}, ...]
 *
 * Expected format for nt_get_indicator_value(name):
 *   INDICATOR:{name}:{"plotName":value, "plotName2":value2}
 */
export function parseIndicatorsResponse(raw) {
  if (!raw || typeof raw !== 'string') return { indicators: [], error: 'empty' };
  if (raw.startsWith('ERROR')) return { indicators: [], error: raw };

  const prefix = 'INDICATORS:';
  const jsonStr = raw.startsWith(prefix) ? raw.substring(prefix.length) : raw;

  try {
    const arr = JSON.parse(jsonStr);
    return { indicators: Array.isArray(arr) ? arr : [], count: Array.isArray(arr) ? arr.length : 0 };
  } catch (err) {
    return { indicators: [], error: `INDICATORS parse failed: ${err.message}`, raw };
  }
}

export function parseIndicatorValueResponse(raw) {
  if (!raw || typeof raw !== 'string') return { error: 'empty' };
  if (raw.startsWith('ERROR')) return { error: raw };

  // Format: INDICATOR:{name}:{json}
  const prefix = 'INDICATOR:';
  if (!raw.startsWith(prefix)) return { raw, error: 'unexpected format' };

  const after = raw.substring(prefix.length);
  const braceIdx = after.indexOf('{');
  if (braceIdx === -1) return { raw, error: 'no json payload' };

  const name = after.substring(0, braceIdx).replace(/:$/, '');
  const jsonStr = after.substring(braceIdx);

  try {
    const plots = JSON.parse(jsonStr);
    return { name, plots };
  } catch (err) {
    return { name, error: `INDICATOR value parse failed: ${err.message}`, raw };
  }
}
