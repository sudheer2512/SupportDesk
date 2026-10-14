import { registerIcons } from '@sd/ui';
import {
  AlarmClock,
  ArrowUpRight,
  BellRing,
  Forward,
  Hash,
  MailCheck,
  MailX,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  Reply,
  ShieldOff,
  StickyNote,
  UserMinus,
  UserRoundCheck,
} from 'lucide';

/**
 * Icons the agent workbench and Command Center need beyond the @sd/ui defaults (bundled Lucide data, no CDN).
 * Imported for its side effect by ticket-ui.ts and the Command Center pages.
 */
registerIcons({
  reply: Reply,
  // Used by <sd-split-layout>'s collapse toggles.
  'panel-left-open': PanelLeftOpen,
  'panel-left-close': PanelLeftClose,
  'panel-right-open': PanelRightOpen,
  'panel-right-close': PanelRightClose,
  'sticky-note': StickyNote,
  'arrow-up-right': ArrowUpRight,
  hash: Hash,
  forward: Forward,
  'bell-ring': BellRing,
  'alarm-clock': AlarmClock,
  'mail-check': MailCheck,
  'mail-x': MailX,
  'user-minus': UserMinus,
  'user-round-check': UserRoundCheck,
  'shield-off': ShieldOff,
} as Parameters<typeof registerIcons>[0]);
