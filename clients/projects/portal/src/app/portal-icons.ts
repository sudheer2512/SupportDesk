import { IconNode, registerIcons } from '@sd/ui';
import {
  ArrowBigUp,
  BadgeCheck,
  CalendarClock,
  Compass,
  FileDown,
  Megaphone,
  MessageSquarePlus,
  Pin,
  Reply,
  Route,
  Signpost,
  TicketCheck,
  Wrench,
} from 'lucide';

/** Extra Lucide icons used only by the customer portal (bundled, offline). */
export function registerPortalIcons(): void {
  registerIcons({
    'arrow-big-up': ArrowBigUp as IconNode,
    'badge-check': BadgeCheck as IconNode,
    'calendar-clock': CalendarClock as IconNode,
    compass: Compass as IconNode,
    'file-down': FileDown as IconNode,
    megaphone: Megaphone as IconNode,
    'message-plus': MessageSquarePlus as IconNode,
    pin: Pin as IconNode,
    reply: Reply as IconNode,
    route: Route as IconNode,
    signpost: Signpost as IconNode,
    'ticket-check': TicketCheck as IconNode,
    wrench: Wrench as IconNode,
  });
}
