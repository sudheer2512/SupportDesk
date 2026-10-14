import { ApiError, Channel, Guid, StatusDto, TicketFormMetaDto } from '@sd/core';
import { SdOption, SdTone, ToastService } from '@sd/ui';
import './workbench-icons';

const CHANNEL_ICONS: Record<string, string> = {
  Email: 'mail',
  Portal: 'globe',
  WebForm: 'file-text',
  Chat: 'message-circle',
  WhatsApp: 'messages',
  Phone: 'phone',
  Sms: 'smartphone',
  Facebook: 'share',
  Instagram: 'image',
  MobileApp: 'smartphone',
  Api: 'code',
  WalkIn: 'user',
  Erp: 'database',
  Internal: 'building',
};

const CHANNEL_LABELS: Record<string, string> = { WebForm: 'Web form', WalkIn: 'Walk-in', MobileApp: 'Mobile app', Sms: 'SMS', Api: 'API', Erp: 'ERP' };

export function channelIcon(channel: Channel | string | null | undefined): string {
  return CHANNEL_ICONS[channel ?? ''] ?? 'message-circle';
}

export function channelLabel(channel: Channel | string | null | undefined): string {
  return channel ? (CHANNEL_LABELS[channel] ?? channel) : '';
}

/** Channels an agent can log a ticket from by hand (new-ticket form). */
export const AGENT_CHANNELS: readonly Channel[] = ['Phone', 'Email', 'WalkIn', 'Chat', 'WhatsApp', 'Portal', 'Internal'];

export function slaTone(state: string | null | undefined): SdTone {
  switch (state) {
    case 'Breached':
      return 'danger';
    case 'AtRisk':
      return 'warning';
    case 'OnTrack':
      return 'success';
    case 'Met':
      return 'info';
    default:
      return 'neutral';
  }
}

export function slaLabel(state: string | null | undefined): string {
  switch (state) {
    case 'AtRisk':
      return 'At risk';
    case 'OnTrack':
      return 'On track';
    case 'None':
    case null:
    case undefined:
      return 'No SLA';
    default:
      return state;
  }
}

export function entitlementTone(status: string | null | undefined): SdTone {
  switch (status) {
    case 'Entitled':
      return 'success';
    case 'Overridden':
      return 'info';
    case 'NotEntitled':
    case 'Expired':
      return 'danger';
    default:
      return 'neutral';
  }
}

export function entitlementLabel(status: string | null | undefined): string {
  switch (status) {
    case 'NotEntitled':
      return 'Not entitled';
    case 'Unknown':
    case null:
    case undefined:
      return 'Entitlement unknown';
    default:
      return status;
  }
}

/** Statuses the ticket may move to: the configured allowed-next list (when set) plus the current one. */
export function allowedStatuses(meta: TicketFormMetaDto | null, currentId: Guid | null): StatusDto[] {
  const all = (meta?.statuses ?? []).filter((s) => s.isActive || s.id === currentId);
  const current = all.find((s) => s.id === currentId);
  if (!current || current.allowedNext.length === 0) return all;
  const allowed = new Set([current.id, ...current.allowedNext]);
  return all.filter((s) => allowed.has(s.id));
}

export function statusOptions(statuses: readonly StatusDto[]): SdOption[] {
  return statuses.map((s) => ({ value: s.id, label: s.name, color: s.color, description: s.pausesSla ? 'Pauses SLA' : null }));
}

export function agentOptions(meta: TicketFormMetaDto | null, departmentId: Guid | null = null, includeLight = false): SdOption[] {
  return (meta?.agents ?? [])
    .filter((a) => (includeLight || !a.isLightAgent) && (!departmentId || a.departmentIds.includes(departmentId)))
    .map((a) => ({
      value: a.userId,
      label: a.name,
      avatar: a.name,
      description: `${a.availability} · ${a.openTickets}/${a.capacity || '∞'} open`,
    }));
}

export function teamOptions(meta: TicketFormMetaDto | null, departmentId: Guid | null): SdOption[] {
  return (meta?.teams ?? []).filter((t) => !departmentId || t.departmentId === departmentId).map((t) => ({ value: t.id, label: t.name }));
}

export function departmentOptions(meta: TicketFormMetaDto | null): SdOption[] {
  return (meta?.departments ?? []).map((d) => ({ value: d.id, label: d.name, description: d.code }));
}

export function lookupOptions(meta: TicketFormMetaDto | null, type: string): SdOption[] {
  return (meta?.lookups ?? []).filter((l) => l.type === type).map((l) => ({ value: l.value, label: l.label }));
}

/** Toasts an API failure with its problem-details title (and detail) — never swallow errors silently. */
export function toastApiError(toast: ToastService, err: unknown, fallback = 'Something went wrong'): ApiError {
  const e = ApiError.from(err);
  // Network / 5xx / 403 / 429 may already have been toasted by the global interceptor (non-silent calls): reuse its
  // de-duplication keys so the two collapse into one toast instead of stacking.
  const key = e.isNetworkError ? 'network' : e.status >= 500 ? `5xx:${e.title}` : e.status === 403 ? '403' : e.status === 429 ? '429' : undefined;
  toast.show({
    kind: e.status === 403 || e.status === 429 ? 'warning' : 'danger',
    title: e.isNetworkError ? 'Connection problem' : e.status === 403 ? 'Not allowed' : e.title || fallback,
    message: e.isNetworkError ? e.userMessage : e.detail && e.detail !== e.title ? e.detail : e.status === 403 ? e.title : null,
    key,
  });
  return e;
}

/** Plain-text excerpt of HTML for previews and accessible labels. */
export function htmlToText(html: string | null | undefined, max = 200): string {
  if (!html) return '';
  const text = html
    .replace(/<(br|\/p|\/div|\/li)[^>]*>/gi, ' ')
    .replace(/<[^>]+>/g, '')
    .replace(/&nbsp;/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/\s+/g, ' ')
    .trim();
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

export function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}
