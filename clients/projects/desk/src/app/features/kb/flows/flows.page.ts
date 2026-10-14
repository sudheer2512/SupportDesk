import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { FlowListItemDto, KnowledgeApi, MeStore } from '@sd/core';
import { SdButton, SdDateTimePipe, SdEmptyState, SdIcon, SdInput, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { KbStatusBadge, KbVisibilityBadge } from '../components/kb-badges.component';
import { KB_BASE, KB_STYLES, formatPercent, registerKbIcons, remote } from '../kb-common';

/** /kb/flows — guided troubleshooting flows with usage counters. */
@Component({
  selector: 'desk-kb-flows',
  imports: [FormsModule, RouterLink, SdButton, SdIcon, SdInput, SdSkeleton, SdEmptyState, SdRelativeTimePipe, SdDateTimePipe, KbStatusBadge, KbVisibilityBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="kb-flows-title">
      <header class="kb-head">
        <div class="kb-head__titles">
          <h2 id="kb-flows-title">Guided troubleshooting</h2>
          <p class="kb-head__sub">Decision trees that walk customers to a fix — or to an agent when they cannot self-serve.</p>
        </div>
        <div class="kb-head__actions">
          <button sdButton variant="ghost" icon="refresh" type="button" [loading]="flows.loading()" (click)="flows.reload()">Refresh</button>
          @if (canAuthor()) {
            <a sdButton variant="primary" icon="plus" [routerLink]="[base, 'flows', 'new']">New flow</a>
          }
        </div>
      </header>

      @if (list().length > 5) {
        <div class="kb-toolbar">
          <div class="kb-filter kb-filter--grow">
            <label for="kb-flow-filter">Filter</label>
            <div class="kb-search">
              <sd-icon name="search" [size]="15" />
              <input sdInput id="kb-flow-filter" type="search" placeholder="Name, product or category" [ngModel]="filter()" (ngModelChange)="filter.set($event)" />
            </div>
          </div>
        </div>
      }

      @if (flows.error(); as err) {
        <div class="kb-error" role="alert">
          <sd-icon name="circle-alert" [size]="16" />
          <p>Could not load the flows. {{ err }}</p>
          <button sdButton size="sm" type="button" icon="refresh" (click)="flows.reload()">Retry</button>
        </div>
      } @else if (flows.loading() && !flows.value()) {
        <div class="skeleton" aria-busy="true" aria-label="Loading flows">
          @for (n of [1, 2, 3]; track n) {
            <sd-skeleton height="52px" radius="var(--sd-radius-sm)" />
          }
        </div>
      } @else if (list().length === 0) {
        <sd-empty-state icon="signpost" title="No troubleshooting flows yet" message="Build a flow of questions that leads customers to a solution, an article or an agent.">
          @if (canAuthor()) {
            <a sdButton variant="primary" icon="plus" [routerLink]="[base, 'flows', 'new']">Create the first flow</a>
          }
        </sd-empty-state>
      } @else if (visible().length === 0) {
        <sd-empty-state icon="search-x" title="No flows match" compact>
          <button sdButton type="button" (click)="filter.set('')">Clear filter</button>
        </sd-empty-state>
      } @else {
        <div class="kb-table-wrap">
          <table class="kb-table">
            <caption class="sd-visually-hidden">Troubleshooting flows</caption>
            <thead>
              <tr>
                <th scope="col">Flow</th>
                <th scope="col">Status</th>
                <th scope="col">Visibility</th>
                <th scope="col">Product / category</th>
                <th scope="col" class="num">Steps</th>
                <th scope="col" class="num">Started</th>
                <th scope="col" class="num">Resolved</th>
                <th scope="col" class="num">Escalated</th>
                <th scope="col">Updated</th>
              </tr>
            </thead>
            <tbody>
              @for (f of visible(); track f.id) {
                <tr>
                  <td class="name">
                    <a [routerLink]="[base, 'flows', f.id]">{{ f.name }}</a>
                    @if (f.description) {
                      <span class="desc">{{ f.description }}</span>
                    }
                  </td>
                  <td><desk-kb-status [status]="f.status" size="sm" /></td>
                  <td><desk-kb-visibility [visibility]="f.visibility" /></td>
                  <td>
                    <span class="sd-small">{{ f.productName ?? '—' }}</span>
                    @if (f.categoryName) {
                      <span class="desc">{{ f.categoryName }}</span>
                    }
                  </td>
                  <td class="num">{{ f.nodeCount }}</td>
                  <td class="num">{{ f.started.toLocaleString() }}</td>
                  <td class="num">
                    {{ f.resolved.toLocaleString() }}
                    <span class="rate">{{ rate(f.resolved, f.started) }}</span>
                  </td>
                  <td class="num">
                    {{ f.escalated.toLocaleString() }}
                    <span class="rate">{{ rate(f.escalated, f.started) }}</span>
                  </td>
                  <td class="nowrap" [attr.title]="f.updatedAt | sdDateTime">{{ f.updatedAt | sdRelativeTime }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
  styles: [
    KB_STYLES,
    `
      .skeleton {
        display: grid;
        gap: 6px;
      }
      .name {
        min-width: 220px;
      }
      .name a {
        display: block;
      }
      .desc {
        display: block;
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
        max-width: 420px;
      }
      .rate {
        display: block;
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
      }
      .nowrap {
        white-space: nowrap;
      }
    `,
  ],
})
export class FlowsPage {
  private readonly api = inject(KnowledgeApi);
  private readonly me = inject(MeStore);

  protected readonly base = KB_BASE;
  protected readonly flows = remote<FlowListItemDto[]>();
  protected readonly filter = signal('');
  protected readonly canAuthor = computed(() => this.me.can('kb.author'));
  protected readonly list = computed(() => [...(this.flows.value() ?? [])].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)));
  protected readonly visible = computed(() => {
    const q = this.filter().trim().toLowerCase();
    if (!q) return this.list();
    return this.list().filter((f) => [f.name, f.description, f.productName, f.categoryName].some((v) => v?.toLowerCase().includes(q)));
  });

  constructor() {
    registerKbIcons();
    this.flows.load(() => this.api.flows());
  }

  protected rate(part: number, total: number): string {
    return total > 0 ? formatPercent((part / total) * 100) : '';
  }
}
