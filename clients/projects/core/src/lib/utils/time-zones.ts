/**
 * Old IANA names that some engines still list (Chromium lists Asia/Calcutta, not Asia/Kolkata), mapped to the
 * current names the API stores — without this a stored "Asia/Kolkata" matches no option and the picker shows blank.
 */
const LEGACY_TIME_ZONES: Readonly<Record<string, string>> = {
  'America/Buenos_Aires': 'America/Argentina/Buenos_Aires',
  'America/Godthab': 'America/Nuuk',
  'America/Indianapolis': 'America/Indiana/Indianapolis',
  'Asia/Calcutta': 'Asia/Kolkata',
  'Asia/Katmandu': 'Asia/Kathmandu',
  'Asia/Rangoon': 'Asia/Yangon',
  'Asia/Saigon': 'Asia/Ho_Chi_Minh',
  'Atlantic/Faeroe': 'Atlantic/Faroe',
  'Europe/Kiev': 'Europe/Kyiv',
  'Pacific/Enderbury': 'Pacific/Kanton',
  'Pacific/Ponape': 'Pacific/Pohnpei',
  'Pacific/Truk': 'Pacific/Chuuk',
};

/** Used when the engine cannot list its time zones (the API validates every value). */
const FALLBACK_TIME_ZONES = [
  'UTC', 'Asia/Kolkata', 'Asia/Dubai', 'Asia/Singapore', 'Asia/Tokyo', 'Australia/Sydney', 'Europe/London', 'Europe/Berlin', 'America/New_York',
  'America/Chicago', 'America/Los_Angeles',
];

/** The current IANA name for a zone (legacy aliases mapped, anything else unchanged). */
export function canonicalTimeZone(zone: string): string {
  return LEGACY_TIME_ZONES[zone] ?? zone;
}

/**
 * IANA time zones for pickers under their current names, with UTC included. The current value is kept as stored when it
 * is not in the list (a legacy name saved earlier, such as Asia/Calcutta), so the picker still shows it selected.
 */
export function timeZoneOptions(current?: string | null): string[] {
  let zones: string[];
  try {
    zones = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.('timeZone') ?? FALLBACK_TIME_ZONES;
  } catch {
    zones = FALLBACK_TIME_ZONES;
  }
  const list = [...new Set(zones.map(canonicalTimeZone))].sort();
  const withUtc = list.includes('UTC') ? list : ['UTC', ...list];
  return current && !withUtc.includes(current) ? [current, ...withUtc] : withUtc;
}
