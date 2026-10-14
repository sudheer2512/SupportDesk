import { ChannelType } from '@sd/core';

/** Form metadata for provider settings and secrets (keys come from GET /channels/providers). */

export interface SettingField {
  label: string;
  type: 'text' | 'number' | 'select' | 'toggle' | 'textarea' | 'url';
  hint?: string;
  placeholder?: string;
  options?: { value: string; label: string }[];
  defaultValue?: string | number | boolean;
}

const SECURITY = [
  { value: 'auto', label: 'Automatic' },
  { value: 'ssl', label: 'SSL/TLS on connect' },
  { value: 'starttls', label: 'STARTTLS' },
  { value: 'none', label: 'None (not recommended)' },
];

export const SETTING_FIELDS: Record<string, SettingField> = {
  pollIntervalSeconds: { label: 'Check every (seconds)', type: 'number', defaultValue: 30, hint: 'How often the mailbox poller fetches new mail (0–3600).' },
  isDefault: { label: 'Default outbound mailbox', type: 'toggle', hint: 'Used for replies on tickets that did not arrive through a mailbox.' },
  imapHost: { label: 'IMAP host', type: 'text', placeholder: 'imap.example.com' },
  imapPort: { label: 'IMAP port', type: 'number', defaultValue: 993 },
  imapSecurity: { label: 'IMAP security', type: 'select', options: SECURITY, defaultValue: 'auto' },
  smtpHost: { label: 'SMTP host', type: 'text', placeholder: 'smtp.example.com' },
  smtpPort: { label: 'SMTP port', type: 'number', defaultValue: 587 },
  smtpSecurity: { label: 'SMTP security', type: 'select', options: SECURITY, defaultValue: 'auto' },
  username: { label: 'User name', type: 'text', hint: 'Defaults to the mailbox address.' },
  authMode: {
    label: 'Authentication',
    type: 'select',
    options: [
      { value: 'password', label: 'Password' },
      { value: 'oauth2', label: 'OAuth 2.0 (XOAUTH2 access token)' },
    ],
    defaultValue: 'password',
  },
  folder: { label: 'Folder', type: 'text', defaultValue: 'INBOX' },
  markSeen: { label: 'Mark fetched mail as read', type: 'toggle', defaultValue: true },
  tenantId: { label: 'Entra ID tenant id', type: 'text', placeholder: 'contoso.onmicrosoft.com or GUID' },
  clientId: { label: 'Client (application) id', type: 'text' },
  mailbox: { label: 'Mailbox (UPN)', type: 'text', hint: 'Defaults to the channel address.' },
  userId: { label: 'Gmail user id', type: 'text', defaultValue: 'me' },
  phoneNumberId: { label: 'Phone number id', type: 'text', hint: 'From the WhatsApp Manager (API setup).' },
  businessAccountId: { label: 'WhatsApp Business account id', type: 'text' },
  graphVersion: { label: 'Graph API version', type: 'text', defaultValue: 'v21.0' },
  verifyToken: { label: 'Webhook verify token', type: 'text', hint: 'Any secret phrase; enter the same value in the Meta webhook setup.' },
  serviceWindowTemplate: { label: 'Template outside the 24-hour window', type: 'text', hint: 'Approved template name used when replying after the window closed.' },
  serviceWindowTemplateLanguage: { label: 'Template language', type: 'text', defaultValue: 'en' },
  pageId: { label: 'Page / Instagram account id', type: 'text' },
  accountSid: { label: 'Account SID', type: 'text', placeholder: 'AC…' },
  messagingServiceSid: { label: 'Messaging service SID', type: 'text', hint: 'Optional; otherwise the channel number sends.' },
  webhookUrl: { label: 'Public webhook URL', type: 'url', hint: 'Only when a proxy rewrites the URL Twilio signs.' },
  statusCallbackUrl: { label: 'Status callback URL', type: 'url' },
  sendUrl: { label: 'Send URL', type: 'url', placeholder: 'https://gateway.example.com/sms' },
  method: {
    label: 'HTTP method',
    type: 'select',
    options: ['POST', 'PUT', 'GET'].map((m) => ({ value: m, label: m })),
    defaultValue: 'POST',
  },
  contentType: {
    label: 'Body format',
    type: 'select',
    options: [
      { value: 'application/json', label: 'JSON' },
      { value: 'application/x-www-form-urlencoded', label: 'Form (URL-encoded)' },
    ],
    defaultValue: 'application/json',
  },
  bodyTemplate: { label: 'Body template', type: 'textarea', hint: 'Placeholders: {{to}} {{from}} {{text}} {{messageId}} {{apiKey}}.' },
  authHeaderName: { label: 'Auth header name', type: 'text', defaultValue: 'Authorization' },
  authHeaderValueTemplate: { label: 'Auth header value', type: 'text', defaultValue: 'Bearer {{apiKey}}' },
  messageIdPath: { label: 'Message id path in the response', type: 'text', defaultValue: 'id', hint: 'Dot path, e.g. data.0.id.' },
  createTicketsForAnsweredCalls: { label: 'Create tickets for answered calls', type: 'toggle', defaultValue: true, hint: 'Missed calls and voicemails always create tickets.' },
  title: { label: 'Widget title', type: 'text', placeholder: 'Chat with us' },
  greeting: { label: 'Greeting', type: 'textarea', defaultValue: 'Hi! How can we help you today?' },
  botEnabled: { label: 'Bot answers first', type: 'toggle', defaultValue: true, hint: 'The AI assistant replies first and hands over to an agent when needed.' },
  requireEmail: { label: 'Ask for e-mail before chatting', type: 'toggle' },
  verifyIdentity: { label: 'Offer e-mail verification', type: 'toggle', defaultValue: true, hint: 'Visitors can verify with a one-time code to link the chat to their contact.' },
  offlineMessage: { label: 'Offline message', type: 'textarea', placeholder: 'Our team is offline right now…' },
};

export const SECRET_LABELS: Record<string, { label: string; hint?: string }> = {
  password: { label: 'Password' },
  accessToken: { label: 'Access token', hint: 'OAuth access token (XOAUTH2) or the Meta permanent token.' },
  clientSecret: { label: 'Client secret' },
  refreshToken: { label: 'Refresh token' },
  appSecret: { label: 'App secret', hint: 'Verifies X-Hub-Signature-256 on every webhook.' },
  pageAccessToken: { label: 'Page access token' },
  authToken: { label: 'Auth token', hint: 'Also validates X-Twilio-Signature.' },
  apiKey: { label: 'API key' },
  webhookSecret: { label: 'Webhook secret', hint: 'Shared secret for the X-Signature HMAC on inbound webhooks.' },
};

/** Extra UI-only setting keys per provider (not listed in the provider catalog). */
export function extraSettingKeys(type: ChannelType | string): string[] {
  return type === 'Email' ? ['isDefault'] : [];
}

export const TYPE_META: Record<string, { label: string; icon: string; addressLabel: string; addressHint: string; addressPlaceholder: string }> = {
  Email: { label: 'E-mail', icon: 'mail', addressLabel: 'Mailbox address', addressHint: 'Customers write to this address.', addressPlaceholder: 'support@example.com' },
  WhatsApp: { label: 'WhatsApp', icon: 'message-circle', addressLabel: 'WhatsApp number', addressHint: 'International format.', addressPlaceholder: '+91 80 4000 1234' },
  Sms: { label: 'SMS', icon: 'smartphone', addressLabel: 'Sender number', addressHint: 'International format.', addressPlaceholder: '+1 555 0100' },
  Chat: { label: 'Live chat', icon: 'messages', addressLabel: 'Widget key', addressHint: 'Leave empty to generate one.', addressPlaceholder: 'wk_…' },
  Voice: { label: 'Voice', icon: 'phone', addressLabel: 'Phone line', addressHint: 'International format.', addressPlaceholder: '+91 80 4000 1000' },
  Facebook: { label: 'Messenger', icon: 'message-square', addressLabel: 'Page id', addressHint: 'The Facebook page id.', addressPlaceholder: '1234567890' },
  Instagram: { label: 'Instagram', icon: 'message-square', addressLabel: 'Account id', addressHint: 'The Instagram professional account id.', addressPlaceholder: '1784…' },
};
