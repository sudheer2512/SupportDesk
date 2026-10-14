import { AppMode } from './mobile-session';

const GUID = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
const DESK_TICKET = new RegExp(`^/tickets/(${GUID})(?:[/?#].*)?$`);
const DESK_TICKET_NUMBER = /^\/tickets\/(\d+)(?:[/?#].*)?$/;
const CUSTOMER_TICKET = new RegExp(`^/customer/tickets/(${GUID})(?:[/?#].*)?$`);
const PORTAL_TICKET = new RegExp(`^/(?:portal/)?[a-z0-9-]+/tickets/(${GUID})(?:[/?#].*)?$`);

/**
 * Maps a server-side link (notification `link`, push payload `link`) to a route of this app.
 * Desk links (`/tickets/{id}`, `/approvals`, `/notifications`) open the agent screens; portal links open the
 * customer ticket. Anything else (admin pages, analytics…) has no mobile screen and returns null.
 */
export function mobileRouteFor(link: string | null | undefined, mode: AppMode): string | null {
  if (!link) return null;
  let path = link.trim();
  try {
    if (/^https?:\/\//i.test(path)) path = new URL(path).pathname;
  } catch {
    return null;
  }
  if (mode === 'agent') {
    const ticket = DESK_TICKET.exec(path);
    if (ticket) return `/agent/tickets/${ticket[1].toLowerCase()}`;
    const byNumber = DESK_TICKET_NUMBER.exec(path);
    if (byNumber) return `/agent/tickets/number/${byNumber[1]}`;
    if (path.startsWith('/approvals')) return '/agent/approvals';
    if (path.startsWith('/notifications')) return '/agent/notifications';
    if (path.startsWith('/agent/')) return path;
    return null;
  }
  const own = CUSTOMER_TICKET.exec(path) ?? PORTAL_TICKET.exec(path);
  return own ? `/customer/tickets/${own[1].toLowerCase()}` : null;
}
