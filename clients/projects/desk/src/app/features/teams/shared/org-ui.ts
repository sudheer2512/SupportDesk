import { ApiError, RoutingMode, WEEK_DAYS, WeeklyHoursDto } from '@sd/core';
import { SdTone } from '@sd/ui';
import { ModuleTab } from '../../customers/shared/module-tabs.component';

/** Secondary navigation of the Teams & Agents module. */
export const TEAMS_TABS: readonly ModuleTab[] = [
  { link: '/teams/departments', label: 'Departments', icon: 'building' },
  { link: '/teams/teams', label: 'Teams', icon: 'users' },
  { link: '/teams/agents', label: 'Agents', icon: 'user' },
  { link: '/teams/skills', label: 'Skills', icon: 'award' },
  { link: '/teams/shifts', label: 'Shifts & leave', icon: 'calendar' },
  { link: '/teams/delegations', label: 'Delegations', icon: 'share' },
  { link: '/teams/monitor', label: 'Monitor', icon: 'radar', permissions: ['supervisor.monitor'] },
];

export const ROUTING_MODE_INFO: Record<RoutingMode, { label: string; help: string }> = {
  Manual: { label: 'Manual', help: 'Tickets wait in the team queue until someone picks or assigns them.' },
  RoundRobin: { label: 'Round robin', help: 'Eligible members take turns in a fixed rotation.' },
  LoadBalanced: { label: 'Load balanced', help: 'The eligible member with the lowest open tickets / capacity ratio.' },
  SkillBased: { label: 'Skill based', help: 'Best match on product, category, territory, tier, language and skill levels, then load.' },
  Territory: { label: 'Territory', help: "Members whose territory matches the customer's service region first, then load." },
};

export function routingModeLabel(mode: string | null | undefined): string {
  return (mode && ROUTING_MODE_INFO[mode as RoutingMode]?.label) || mode || '—';
}

export function availabilityTone(availability: string | null | undefined): SdTone {
  switch (availability) {
    case 'Online':
      return 'success';
    case 'Busy':
      return 'warning';
    case 'Away':
      return 'info';
    default:
      return 'neutral';
  }
}

export function agentStatusTone(status: string | null | undefined): SdTone {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Invited':
      return 'info';
    case 'Deactivated':
      return 'neutral';
    default:
      return 'neutral';
  }
}

export function leaveStatusTone(status: string | null | undefined): SdTone {
  switch (status) {
    case 'Approved':
      return 'success';
    case 'Requested':
      return 'warning';
    case 'Rejected':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** "Mon–Fri 09:00–18:00 · Sat 10:00–14:00" style summary of weekly hours. */
export function weeklySummary(weekly: WeeklyHoursDto | null | undefined): string {
  if (!weekly) return '—';
  const parts: { days: string[]; hours: string }[] = [];
  for (const day of WEEK_DAYS) {
    const hours = (weekly[day] ?? []).map((i) => `${i.start}–${i.end}`).join(', ');
    if (!hours) continue;
    const last = parts[parts.length - 1];
    const prevDay = WEEK_DAYS[WEEK_DAYS.indexOf(day) - 1];
    if (last && last.hours === hours && last.days[last.days.length - 1] === prevDay) last.days.push(day);
    else parts.push({ days: [day], hours });
  }
  if (parts.length === 0) return 'No working hours';
  return parts
    .map((p) => (p.days.length > 2 ? `${p.days[0].slice(0, 3)}–${p.days[p.days.length - 1].slice(0, 3)}` : p.days.map((d) => d.slice(0, 3)).join(', ')) + ' ' + p.hours)
    .join(' · ');
}

/** Minutes → "2h 30m" / "3d 4h" (targets and durations). */
export function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined) return '—';
  const m = Math.round(minutes);
  if (m < 60) return `${m}m`;
  const days = Math.floor(m / 1440);
  const hours = Math.floor((m % 1440) / 60);
  const mins = m % 60;
  return [days ? `${days}d` : '', hours ? `${hours}h` : '', mins ? `${mins}m` : ''].filter(Boolean).join(' ');
}

/** IANA time zones for pickers (shared with the profile and portal pickers; the API validates every value). */
export { timeZoneOptions } from '@sd/core';

/** User-facing message for an API failure. */
export function errorText(err: unknown): string {
  return ApiError.from(err).userMessage;
}

/** Form layout fragments shared by the module's dialogs (tokens only). */
export const FORM_STYLES = `
  .form { display: grid; gap: var(--sd-space-3); }
  .two { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .three { display: grid; gap: var(--sd-space-3); grid-template-columns: repeat(3, minmax(0, 1fr)); }
  @media (max-width: 640px) { .two, .three { grid-template-columns: 1fr; } }
  .error { color: var(--sd-danger); font-size: var(--sd-text-sm); }
  .hint { color: var(--sd-text-3); font-size: var(--sd-text-sm); }
  .checks { display: flex; flex-wrap: wrap; gap: var(--sd-space-3); }
`;

/** Page fragments: toolbar, cards grid, pills. */
export const PAGE_STYLES = `
  .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2); margin-bottom: var(--sd-space-4); }
  .toolbar .sd-spacer { flex: 1; }
  .toolbar select, .toolbar input[type='search'] { max-width: 240px; }
  .grid { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fill, minmax(min(100%, 340px), 1fr)); }
  .muted { color: var(--sd-text-3); }
  .small { font-size: var(--sd-text-sm); }
  .xs { font-size: var(--sd-text-xs); }
  .pills { display: flex; flex-wrap: wrap; gap: 4px; }
  .who { display: flex; align-items: center; gap: var(--sd-space-2); min-width: 0; }
  .who > span { min-width: 0; display: grid; }
  .error-box { border: 1px solid var(--sd-danger-border); color: var(--sd-danger); background: var(--sd-danger-bg); padding: var(--sd-space-3);
    border-radius: var(--sd-radius-md); display: flex; gap: var(--sd-space-3); align-items: center; flex-wrap: wrap; }
  .load { display: grid; grid-template-columns: minmax(60px, 1fr) auto; gap: 6px; align-items: center; min-width: 110px; }
  .row-actions { white-space: nowrap; }
  .row-actions > * { flex: none; }
`;

/** Percent (0–100) of load for bar widths. */
export function loadPercent(open: number, capacity: number): number {
  if (!capacity || capacity <= 0) return open > 0 ? 100 : 0;
  return Math.min(100, Math.round((open / capacity) * 100));
}
