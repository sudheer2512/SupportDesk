import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TicketLinkDto, TicketsApi } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdIcon, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { toastApiError } from '../../shared/ticket-ui';
import { TicketActions } from '../ticket-actions.service';
import { TicketContext } from '../ticket-context.service';

const LINK_LABELS: Record<string, [string, string]> = {
  Related: ['Related to', 'Related to'],
  Duplicate: ['Duplicate of', 'Has duplicate'],
  Blocks: ['Blocks', 'Blocked by'],
  CausedBy: ['Caused by', 'Causes'],
  SplitFrom: ['Split from', 'Split into'],
  ClonedFrom: ['Cloned from', 'Cloned into'],
};

/** Parent / child tickets, linked tickets and external references (ERP documents, origin systems). */
@Component({
  selector: 'desk-related-panel',
  imports: [RouterLink, SdBadge, SdButton, SdEmptyState, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (ctx.ticket(); as t) {
      @if (t.parentTicketId) {
        <section>
          <h3>Parent</h3>
          <a class="row" [routerLink]="['/tickets', t.parentTicketId]">
            <sd-icon name="arrow-up" [size]="14" /> #{{ t.parentTicketNumber }}
          </a>
        </section>
      }

      <section>
        <h3>Child tickets ({{ t.children.length }})</h3>
        @for (c of t.children; track c.id) {
          <a class="row" [routerLink]="['/tickets', c.id]">
            <span class="num">#{{ c.number }}</span>
            <span class="sd-truncate grow">{{ c.subject }}</span>
            <sd-badge size="sm">{{ c.status }}</sd-badge>
          </a>
        } @empty {
          <p class="sd-small sd-muted">No child tickets.</p>
        }
      </section>

      <section>
        <div class="head">
          <h3>Linked tickets ({{ t.links.length }})</h3>
          @if (t.can.edit) {
            <button sdButton size="sm" variant="ghost" icon="link" type="button" (click)="actions.link()">Link…</button>
          }
        </div>
        @for (l of t.links; track l.id) {
          <div class="row">
            <span class="rel sd-xs">{{ linkLabel(l) }}</span>
            <a class="grow sd-truncate" [routerLink]="['/tickets', l.ticketId]">#{{ l.number }} · {{ l.subject }}</a>
            <sd-badge size="sm">{{ l.status }}</sd-badge>
            @if (t.can.edit) {
              <button type="button" class="icon" [attr.aria-label]="'Remove link to #' + l.number" (click)="unlink(l)"><sd-icon name="x" [size]="14" /></button>
            }
          </div>
        } @empty {
          <p class="sd-small sd-muted">No linked tickets.</p>
        }
      </section>

      <section>
        <h3>External references ({{ t.externalReferences.length }})</h3>
        @for (r of t.externalReferences; track r.id) {
          <div class="row">
            <sd-icon name="database" [size]="14" />
            <span class="grow sd-truncate">
              <strong>{{ r.system }}</strong> {{ r.recordType }} {{ r.recordNumber ?? '' }}
              @if (r.isOrigin) {
                <sd-badge size="sm" tone="info">origin</sd-badge>
              }
            </span>
            @if (safeUrl(r.url); as url) {
              <a [href]="url" target="_blank" rel="noopener noreferrer" [attr.aria-label]="'Open ' + r.system + ' record'"><sd-icon name="external-link" [size]="14" /></a>
            }
          </div>
        } @empty {
          <p class="sd-small sd-muted">No external references.</p>
        }
      </section>

      @if (!hasAnything()) {
        <sd-empty-state icon="link" title="Nothing related yet" message="Link duplicates, blockers or follow-up tickets here." compact />
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-4);
    }
    section {
      display: grid;
      gap: 4px;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    h3 {
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text-3);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .row {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      padding: 6px 8px;
      border-radius: var(--sd-radius-sm);
      color: inherit;
      text-decoration: none;
    }
    a.row:hover,
    .row:hover {
      background: var(--sd-surface-2);
    }
    .grow {
      flex: 1;
      min-width: 0;
      color: inherit;
    }
    .num {
      color: var(--sd-text-2);
      font-variant-numeric: tabular-nums;
    }
    .rel {
      flex: none;
      color: var(--sd-text-3);
    }
    .icon {
      display: inline-flex;
      padding: 4px;
      border: 0;
      border-radius: var(--sd-radius-xs);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .icon:hover {
      color: var(--sd-danger);
    }
  `,
})
export class RelatedPanelComponent {
  protected readonly ctx = inject(TicketContext);
  protected readonly actions = inject(TicketActions);
  private readonly tickets = inject(TicketsApi);
  private readonly toast = inject(ToastService);

  protected readonly hasAnything = computed(() => {
    const t = this.ctx.ticket();
    return !!t && (!!t.parentTicketId || t.children.length > 0 || t.links.length > 0 || t.externalReferences.length > 0);
  });

  protected linkLabel(l: TicketLinkDto): string {
    const labels = LINK_LABELS[l.linkType] ?? [l.linkType, l.linkType];
    return l.direction === 'Inbound' || l.direction === 'incoming' ? labels[1] : labels[0];
  }

  /** Only http(s) links are rendered as anchors. */
  protected safeUrl(url: string | null): string | null {
    return url && /^https?:\/\//i.test(url) ? url : null;
  }

  protected async unlink(l: TicketLinkDto): Promise<void> {
    const id = this.ctx.id();
    if (!id) return;
    try {
      await firstValueFrom(this.tickets.removeLink(id, l.id));
      this.toast.success('Link removed');
      this.ctx.refresh();
    } catch (err) {
      toastApiError(this.toast, err, 'Could not remove the link');
    }
  }
}
