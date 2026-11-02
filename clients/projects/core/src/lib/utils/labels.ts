/** Enum values whose readable form is not just the words split apart (acronyms, product names). */
const KNOWN_LABELS: Readonly<Record<string, string>> = {
  Api: 'API',
  AzureDevOps: 'Azure DevOps',
  Erp: 'ERP',
  GenericSms: 'Generic SMS gateway',
  GenericVoice: 'Generic voice webhook',
  GitHub: 'GitHub',
  Imap: 'IMAP',
  MetaCloud: 'WhatsApp Cloud API (Meta)',
  MetaGraph: 'Messenger / Instagram (Meta Graph)',
  Microsoft365: 'Microsoft 365',
  Sms: 'SMS',
  Smtp: 'SMTP',
  WhatsApp: 'WhatsApp',
  WingsBooks: 'Wings Books',
  WingsErp: 'Wings ERP',
};

/**
 * A readable label for an enum value the API sends as a string: "OnHold" → "On hold", "PastDue" → "Past due",
 * "Sms" → "SMS", "Microsoft365" → "Microsoft 365", "skill_based" → "Skill based".
 */
export function enumLabel(value: string | null | undefined): string {
  if (!value) return '';
  const known = KNOWN_LABELS[value];
  if (known) return known;
  const words = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .replace(/[_.]+/g, ' ')
    .trim();
  return words.charAt(0).toUpperCase() + words.slice(1).toLowerCase();
}
