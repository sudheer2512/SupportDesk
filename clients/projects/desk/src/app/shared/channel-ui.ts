import { MessageSquare, PhoneIncoming, PhoneMissed, PhoneOutgoing, RadioTower, Voicemail, Volume2, VolumeX } from 'lucide';
import { ChannelAccountStatus, ConversationStatus, MessageStatus } from '@sd/core';
import { IconNode, SdTone, registerIcons } from '@sd/ui';

/** Omnichannel presentation helpers shared by the inbox, the live-chat console and channel administration. */

registerIcons({
  'message-square': MessageSquare as IconNode,
  'phone-incoming': PhoneIncoming as IconNode,
  'phone-missed': PhoneMissed as IconNode,
  'phone-outgoing': PhoneOutgoing as IconNode,
  'radio-tower': RadioTower as IconNode,
  voicemail: Voicemail as IconNode,
  'volume-on': Volume2 as IconNode,
  'volume-off': VolumeX as IconNode,
});

const CHANNEL_META: Record<string, { label: string; icon: string }> = {
  Email: { label: 'E-mail', icon: 'mail' },
  WhatsApp: { label: 'WhatsApp', icon: 'message-circle' },
  Sms: { label: 'SMS', icon: 'smartphone' },
  Chat: { label: 'Live chat', icon: 'messages' },
  Phone: { label: 'Phone', icon: 'phone' },
  Voice: { label: 'Voice', icon: 'phone' },
  Facebook: { label: 'Messenger', icon: 'message-square' },
  Instagram: { label: 'Instagram', icon: 'message-square' },
  WebForm: { label: 'Web form', icon: 'globe' },
  Portal: { label: 'Portal', icon: 'globe' },
  Api: { label: 'API', icon: 'code' },
};

export function channelLabel(channel: string): string {
  return CHANNEL_META[channel]?.label ?? channel;
}

export function channelIcon(channel: string): string {
  return CHANNEL_META[channel]?.icon ?? 'message-circle';
}

/** Inbox channel filter options (conversation channels). */
export const INBOX_CHANNELS = ['Email', 'WhatsApp', 'Sms', 'Chat', 'Phone', 'Facebook', 'Instagram'] as const;

export function conversationStatusLabel(status: ConversationStatus): string {
  switch (status) {
    case 'BotActive':
      return 'Bot';
    case 'WaitingForAgent':
      return 'Waiting for agent';
    case 'AgentActive':
      return 'With agent';
    default:
      return status;
  }
}

export function conversationStatusTone(status: ConversationStatus): SdTone {
  switch (status) {
    case 'WaitingForAgent':
      return 'warning';
    case 'AgentActive':
      return 'success';
    case 'BotActive':
      return 'info';
    case 'Closed':
      return 'neutral';
    default:
      return 'primary';
  }
}

export function deliveryTone(status: MessageStatus | string): SdTone {
  switch (status) {
    case 'Delivered':
    case 'Read':
      return 'success';
    case 'Sent':
      return 'primary';
    case 'Failed':
    case 'Rejected':
    case 'Bounced':
      return 'danger';
    case 'Queued':
    case 'Sending':
    case 'Retrying':
      return 'warning';
    default:
      return 'neutral';
  }
}

export function deliveryIcon(status: MessageStatus | string): string {
  switch (status) {
    case 'Read':
      return 'check-check';
    case 'Delivered':
      return 'check-check';
    case 'Sent':
      return 'check';
    case 'Failed':
    case 'Bounced':
      return 'circle-x';
    default:
      return 'clock';
  }
}

export function accountStatusTone(status: ChannelAccountStatus): SdTone {
  return status === 'Connected' ? 'success' : status === 'Error' ? 'danger' : status === 'Disabled' ? 'neutral' : 'warning';
}

/** Typed accessor for loose JSON metadata objects. */
export function metaString(meta: { [key: string]: unknown } | null | undefined, key: string): string | null {
  const v = meta?.[key];
  return typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : null;
}

/** Saves a blob as a file (object URL + temporary link). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** Copies text to the clipboard; resolves false when the browser refuses. */
export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
