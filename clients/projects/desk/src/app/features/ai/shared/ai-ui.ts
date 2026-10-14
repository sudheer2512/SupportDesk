import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AiProposalChangeDto, ApiError, isApiError } from '@sd/core';
import { SdIcon, SdTone } from '@sd/ui';

/** "82%" from a 0..1 confidence. */
export function pct(value: number | null | undefined, digits = 0): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${(value * 100).toFixed(digits)}%`;
}

/** Human duration for hours (minutes under an hour, days above two days). */
export function hours(value: number | null | undefined): string {
  if (value === null || value === undefined) return '—';
  if (value < 1) return `${Math.max(1, Math.round(value * 60))} min`;
  if (value < 48) return `${value.toFixed(value < 10 ? 1 : 0)} h`;
  return `${(value / 24).toFixed(1)} days`;
}

export function money(value: number | null | undefined, currency = 'USD'): string {
  const v = value ?? 0;
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: v < 1 ? 4 : 2 }).format(v);
  } catch {
    return `${v.toFixed(2)} ${currency}`;
  }
}

export function compact(value: number | null | undefined): string {
  return new Intl.NumberFormat(undefined, { notation: 'compact', maximumFractionDigits: 1 }).format(value ?? 0);
}

export function riskTone(risk: string | null | undefined): SdTone {
  return risk === 'High' ? 'danger' : risk === 'Medium' ? 'warning' : 'success';
}

export function proposalTone(status: string): SdTone {
  switch (status) {
    case 'Applied':
      return 'success';
    case 'Proposed':
      return 'primary';
    case 'Rejected':
    case 'Expired':
      return 'neutral';
    case 'Reverted':
      return 'warning';
    case 'Failed':
      return 'danger';
    default:
      return 'info';
  }
}

export function executionTone(status: string): SdTone {
  return status === 'Succeeded' ? 'success' : status === 'Failed' ? 'danger' : status === 'Disabled' ? 'neutral' : 'warning';
}

export function sentimentTone(sentiment: string | null | undefined): SdTone {
  switch (sentiment) {
    case 'Positive':
      return 'success';
    case 'Negative':
      return 'warning';
    case 'Frustrated':
      return 'danger';
    case 'Neutral':
      return 'info';
    default:
      return 'neutral';
  }
}

export function confidenceTone(value: number | null | undefined): SdTone {
  if (value === null || value === undefined) return 'neutral';
  return value >= 0.8 ? 'success' : value >= 0.6 ? 'primary' : value >= 0.4 ? 'warning' : 'danger';
}

const ACRONYMS = new Set(['sla', 'csat', 'ai', 'nl', 'id', 'pii', 'api', 'kb']);

/** "reply_suggestion" → "Reply suggestion". */
export function humanize(key: string | null | undefined): string {
  if (!key) return '';
  const s = key.replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase();
  const words = s.split(' ').map((w) => (ACRONYMS.has(w) ? w.toUpperCase() : w));
  const text = words.join(' ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** User-facing message for AI refusals (the API's codes carry specific guidance). */
export function aiErrorMessage(err: unknown): string {
  if (!isApiError(err)) return 'Something went wrong.';
  switch (err.code) {
    case 'ai_disabled':
      return 'AI is turned off for this organisation. An administrator can enable it in AI settings.';
    case 'ai_feature_disabled':
      return err.userMessage + ' An administrator can enable it in AI settings.';
    case 'feature_not_in_plan':
      return 'This AI feature is not included in your plan.';
    case 'ai_budget_exceeded':
      return err.userMessage;
    case 'ai_role_restricted':
      return 'Your role cannot use this AI feature.';
    case 'ai_provider_error':
    case 'ai_invalid_output':
      return err.userMessage;
    default:
      return err.userMessage;
  }
}

/**
 * Field errors from a validation problem. The API sends `errors` as field → message (or message[]); both are
 * normalised to field → first message.
 */
export function fieldErrors(err: unknown): Record<string, string> {
  if (!isApiError(err)) return {};
  const raw = (err.problem?.errors ?? {}) as Record<string, string | string[]>;
  const result: Record<string, string> = {};
  for (const [k, v] of Object.entries(raw)) result[k] = Array.isArray(v) ? (v[0] ?? '') : String(v);
  return result;
}

export function isCode(err: unknown, code: string): boolean {
  return err instanceof ApiError && err.code === code;
}

/** Before → after list for an AI proposal (names resolved by the API). */
@Component({
  selector: 'desk-ai-changes',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (changes().length) {
      <dl class="changes">
        @for (c of changes(); track c.field) {
          <div class="change">
            <dt>{{ label(c.field) }}</dt>
            <dd>
              @if (isText(c)) {
                <span class="text">{{ c.after }}</span>
              } @else {
                <span class="before" [class.empty]="!c.before">{{ c.before || 'empty' }}</span>
                <sd-icon name="arrow-right" [size]="14" aria-hidden="true" />
                <span class="after">{{ c.after || 'empty' }}</span>
              }
            </dd>
          </div>
        }
      </dl>
    }
  `,
  styles: `
    .changes {
      display: grid;
      gap: 6px;
      margin: 0;
    }
    .change {
      display: grid;
      grid-template-columns: minmax(90px, max-content) 1fr;
      gap: 10px;
      align-items: baseline;
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    dd {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin: 0;
      min-width: 0;
    }
    .before {
      color: var(--sd-text-2);
      text-decoration: line-through;
      text-decoration-color: var(--sd-text-3);
    }
    .before.empty {
      text-decoration: none;
      font-style: italic;
      color: var(--sd-text-3);
    }
    .after {
      font-weight: var(--sd-weight-semibold);
    }
    .text {
      white-space: pre-line;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class AiChangesComponent {
  readonly changes = input<readonly AiProposalChangeDto[]>([]);
  protected readonly label = humanize;
  protected isText(c: AiProposalChangeDto): boolean {
    return c.before === null && (c.field === 'reply to customer' || c.field === 'internal note');
  }
}

/** Small horizontal meter for a 0..1 value (confidence, likelihood, similarity). */
@Component({
  selector: 'desk-ai-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="track" role="meter" [attr.aria-valuenow]="percent()" aria-valuemin="0" aria-valuemax="100" [attr.aria-label]="label()">
      <span class="fill" [style.width.%]="percent()" [attr.data-tone]="tone()"></span>
    </span>
    <span class="value">{{ percent() }}%</span>
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      min-width: 110px;
    }
    .track {
      position: relative;
      flex: 1;
      height: 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .fill {
      position: absolute;
      inset: 0 auto 0 0;
      border-radius: inherit;
      background: var(--sd-primary);
    }
    .fill[data-tone='success'] {
      background: var(--sd-success);
    }
    .fill[data-tone='warning'] {
      background: var(--sd-warning);
    }
    .fill[data-tone='danger'] {
      background: var(--sd-danger);
    }
    .value {
      min-width: 36px;
      text-align: end;
      font-variant-numeric: tabular-nums;
      font-size: var(--sd-text-xs);
      color: var(--sd-text-2);
    }
  `,
})
export class AiMeterComponent {
  readonly value = input<number | null>(0);
  readonly label = input('Confidence');
  protected readonly percent = computed(() => Math.round(Math.max(0, Math.min(1, this.value() ?? 0)) * 100));
  protected readonly tone = computed(() => confidenceTone(this.value()));
}
