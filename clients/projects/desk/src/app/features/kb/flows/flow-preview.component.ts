import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FlowDefinitionDto, FlowNodeDto } from '@sd/core';
import { SdBadge, SdButton, SdIcon } from '@sd/ui';
import { KB_BASE } from '../kb-common';
import { nodeLabel } from './flow-layout';

interface Step {
  nodeId: string;
  /** Answer chosen to get here (null for the start). */
  via: string | null;
}

/**
 * Walks a flow the way a customer would: question → answer → … → solution / article / escalation,
 * with Back and Restart. Nothing is recorded.
 */
@Component({
  selector: 'desk-kb-flow-preview',
  imports: [RouterLink, SdButton, SdIcon, SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="preview">
      <div class="frame">
        <div class="bar">
          <sd-icon name="signpost" [size]="16" />
          <strong class="sd-truncate">{{ name() || 'Untitled flow' }}</strong>
          <span class="sd-spacer"></span>
          <sd-badge size="sm" variant="outline">Preview — nothing is recorded</sd-badge>
        </div>

        @if (path().length > 1) {
          <ol class="trail" aria-label="Answers so far">
            @for (s of path().slice(1); track $index) {
              <li><sd-icon name="check" [size]="12" /> {{ s.via }}</li>
            }
          </ol>
        }

        <div class="card" #card tabindex="-1" aria-live="polite" aria-atomic="true">
          @if (current(); as n) {
            @switch (n.type) {
              @case ('question') {
                <p class="kicker">Step {{ path().length }}</p>
                <h3 class="question">{{ n.text || nodeLabel(n) }}</h3>
                @if (n.options.length) {
                  <div class="choices" role="group" aria-label="Answers">
                    @for (opt of n.options; track $index) {
                      <button sdButton block type="button" iconRight="chevron-right" [disabled]="!exists(opt.next)" (click)="choose(opt.label, opt.next)">
                        {{ opt.label || '(no label)' }}
                        @if (!exists(opt.next)) {
                          <span class="sd-xs"> — not connected</span>
                        }
                      </button>
                    }
                  </div>
                } @else {
                  <p class="warn"><sd-icon name="triangle-alert" [size]="14" /> This question has no answers yet — the customer would be stuck here.</p>
                }
              }
              @case ('solution') {
                <p class="kicker solution"><sd-icon name="lightbulb" [size]="14" /> Suggested solution</p>
                <div class="sd-prose" [innerHTML]="n.text || '<p>(No solution text yet.)</p>'"></div>
                <p class="end"><sd-icon name="circle-check" [size]="14" /> The customer sees “Did this solve your problem?” — a yes resolves the flow.</p>
              }
              @case ('article') {
                <p class="kicker article"><sd-icon name="file-text" [size]="14" /> Recommended article</p>
                @if (n.text) {
                  <p>{{ n.text }}</p>
                }
                @if (n.articleId) {
                  <a class="article-link" [routerLink]="[base, 'articles', n.articleId]" target="_blank" rel="noopener">
                    <sd-icon name="book-open" [size]="16" /> {{ articleTitles()[n.articleId] ?? 'Open the linked article' }}
                    <sd-icon name="external-link" [size]="13" />
                  </a>
                } @else {
                  <p class="warn"><sd-icon name="triangle-alert" [size]="14" /> No article is linked to this step.</p>
                }
              }
              @case ('escalate') {
                <p class="kicker escalate"><sd-icon name="headphones" [size]="14" /> Talk to support</p>
                <p class="message">{{ n.text || 'Our team will take it from here.' }}</p>
                <p class="end"><sd-icon name="ticket" [size]="14" /> The customer is offered a ticket with their {{ path().length - 1 }} answer{{ path().length === 2 ? '' : 's' }} attached.</p>
              }
            }
          } @else {
            <p class="warn"><sd-icon name="triangle-alert" [size]="14" /> The flow has no start step.</p>
          }
        </div>

        <div class="nav">
          <button sdButton variant="ghost" icon="arrow-left" type="button" [disabled]="path().length <= 1" (click)="back()">Back</button>
          <span class="sd-spacer"></span>
          <button sdButton variant="ghost" icon="refresh" type="button" [disabled]="path().length <= 1" (click)="restart()">Restart</button>
        </div>
      </div>
    </div>
  `,
  styles: `
    .preview {
      display: flex;
      justify-content: center;
      padding: var(--sd-space-5) var(--sd-space-3);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface-sunken);
    }
    .frame {
      display: grid;
      gap: var(--sd-space-3);
      width: min(560px, 100%);
    }
    .bar {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
      color: var(--sd-text-2);
    }
    .trail {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .trail li {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 8px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-size: var(--sd-text-xs);
    }
    .card {
      display: grid;
      gap: var(--sd-space-3);
      padding: var(--sd-space-5);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-md);
      outline: none;
    }
    .kicker {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .kicker.solution {
      color: var(--sd-success);
    }
    .kicker.article {
      color: var(--sd-info);
    }
    .kicker.escalate {
      color: var(--sd-warning);
    }
    .question {
      font-size: var(--sd-text-lg);
      font-weight: var(--sd-weight-semibold);
    }
    .choices {
      display: grid;
      gap: 6px;
    }
    .choices button {
      justify-content: space-between;
      height: auto;
      min-height: var(--sd-control-h-lg);
      white-space: normal;
      text-align: left;
    }
    .article-link {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border: 1px solid var(--sd-info-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-info-bg);
      font-weight: var(--sd-weight-medium);
    }
    .message {
      font-size: var(--sd-text-md);
    }
    .end,
    .warn {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: var(--sd-text-sm);
    }
    .end {
      color: var(--sd-text-3);
    }
    .warn {
      color: var(--sd-warning);
    }
    .nav {
      display: flex;
      align-items: center;
    }
  `,
})
export class FlowPreview {
  readonly definition = input.required<FlowDefinitionDto>();
  readonly name = input('');
  readonly articleTitles = input<Readonly<Record<string, string>>>({});

  protected readonly base = KB_BASE;
  protected readonly nodeLabel = nodeLabel;
  private readonly card = viewChild<ElementRef<HTMLElement>>('card');
  private readonly byId = computed(() => new Map(this.definition().nodes.map((n) => [n.id, n])));
  protected readonly path = signal<Step[]>([]);
  protected readonly current = computed<FlowNodeDto | null>(() => {
    const last = this.path().at(-1);
    return last ? (this.byId().get(last.nodeId) ?? null) : null;
  });

  constructor() {
    effect(() => {
      const def = this.definition();
      untracked(() => this.path.set(def.startNodeId && this.byId().has(def.startNodeId) ? [{ nodeId: def.startNodeId, via: null }] : []));
    });
  }

  protected exists(id: string | null | undefined): boolean {
    return !!id && this.byId().has(id);
  }

  protected choose(label: string, next: string): void {
    if (!this.exists(next)) return;
    this.path.update((p) => [...p, { nodeId: next, via: label || '(no label)' }]);
    this.focusCard();
  }

  protected back(): void {
    this.path.update((p) => (p.length > 1 ? p.slice(0, -1) : p));
    this.focusCard();
  }

  protected restart(): void {
    this.path.update((p) => p.slice(0, 1));
    this.focusCard();
  }

  private focusCard(): void {
    queueMicrotask(() => this.card()?.nativeElement.focus());
  }
}
