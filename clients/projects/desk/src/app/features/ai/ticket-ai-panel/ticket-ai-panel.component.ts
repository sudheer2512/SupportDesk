import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import {
  AiApi,
  AiProposalDto,
  ClassificationSuggestionDto,
  DuplicateTicketCandidateDto,
  KnowledgeDraftDto,
  MeStore,
  ResolutionPredictionDto,
  RootCauseDto,
  SentimentDto,
  SimilarTicketDto,
  SuggestedReplyDto,
  TicketDetailDto,
  TicketSummaryDto,
  TicketsApi,
} from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdDialogService, SdIcon, SdRelativeTimePipe, SdSkeleton, SdTooltip, ToastService } from '@sd/ui';
import { Observable, forkJoin } from 'rxjs';
import { AiProposalCardComponent } from '../shared/proposal-card.component';
import { AiMeterComponent, aiErrorMessage, hours, humanize, pct, sentimentTone } from '../shared/ai-ui';

type Section = 'summary' | 'sentiment' | 'prediction' | 'classify' | 'reply' | 'similar' | 'rootCause' | 'draft' | 'proposals' | 'agents';

/**
 * AI assistance for one ticket, for the workbench context panel (or the full-page /ai/tickets/:id view):
 * summary, sentiment, resolution-time estimate, classification with apply, a grounded reply draft with numbered
 * citations ("insert into composer"), similar and duplicate tickets (with merge), root-cause suggestions, a
 * knowledge-article draft, pending AI proposals for the ticket and "run agents".
 *
 * `<desk-ticket-ai-panel [ticketId]="id" (insertReply)="composer.insert($event)" (ticketChanged)="reload()" />`
 */
@Component({
  selector: 'desk-ticket-ai-panel',
  imports: [FormsModule, RouterLink, SdButton, SdBadge, SdIcon, SdCheckbox, SdSkeleton, SdTooltip, SdRelativeTimePipe, AiMeterComponent, AiProposalCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel" aria-label="AI assistance">
      <!-- Summary -->
      <section class="block" aria-labelledby="ai-sum">
        <header>
          <h3 id="ai-sum"><sd-icon name="file-text" [size]="15" />Summary</h3>
          <button sdButton size="sm" variant="ghost" icon="refresh" [loading]="busy('summary')" (click)="summarize()">{{ summary() || ticket()?.aiSummary ? 'Refresh' : 'Summarize' }}</button>
        </header>
        @if (summary(); as s) {
          <p class="text">{{ s.summary }}</p>
          @if (s.currentStatus) {
            <p class="status"><strong>Status:</strong> {{ s.currentStatus }}</p>
          }
          @if (s.pendingActions.length) {
            <ul class="actions-list">
              @for (a of s.pendingActions; track a) {
                <li>{{ a }}</li>
              }
            </ul>
          }
          <p class="foot sd-xs sd-muted">Generated {{ s.generatedAt | sdRelativeTime }} · {{ s.provider }}</p>
        } @else if (ticket()?.aiSummary; as stored) {
          <p class="text pre">{{ stored }}</p>
        } @else if (!busy('summary')) {
          <p class="sd-small sd-muted">No summary yet.</p>
        }
        @if (errors()['summary']; as e) {
          <p class="err" role="alert">{{ e }}</p>
        }
      </section>

      <!-- Sentiment + prediction -->
      <section class="block two" aria-label="Sentiment and expected resolution">
        <div>
          <header>
            <h3><sd-icon name="message-circle" [size]="15" />Sentiment</h3>
            <button sdButton size="sm" variant="ghost" icon="refresh" iconOnly aria-label="Analyse sentiment again" sdTooltip="Analyse again" [loading]="busy('sentiment')" (click)="analyzeSentiment()"></button>
          </header>
          @if (sentiment(); as s) {
            <div class="row">
              <sd-badge [tone]="tone(s.sentiment)" [icon]="s.frustrated ? 'triangle-alert' : null">{{ s.sentiment === 'Unknown' ? 'Not analysed' : s.sentiment }}</sd-badge>
              @if (s.score !== null) {
                <span class="sd-small sd-muted">score {{ s.score.toFixed(2) }}</span>
              }
            </div>
            @if (s.messages.length) {
              <div class="dots" aria-label="Sentiment per customer message">
                @for (m of s.messages; track m.threadId) {
                  <span class="dot" [attr.data-s]="m.sentiment" [sdTooltip]="'#' + m.seq + ': ' + m.sentiment"></span>
                }
              </div>
            }
            @if (s.frustrated) {
              <p class="warn sd-small">Acknowledge the impact and give a concrete next step.</p>
            }
          } @else if (busy('sentiment')) {
            <sd-skeleton [lines]="2" />
          }
        </div>
        <div>
          <header>
            <h3><sd-icon name="hourglass" [size]="15" />Expected resolution</h3>
          </header>
          @if (prediction(); as p) {
            @if (p.medianHours !== null) {
              <p class="big">{{ fmtHours(p.medianHours) }}</p>
              <p class="sd-small">typical range {{ fmtHours(p.p25Hours) }} – {{ fmtHours(p.p75Hours) }}</p>
              <p class="sd-xs sd-muted">{{ p.sampleSize }} similar ticket(s) · {{ p.basis }}</p>
              @if (p.predictedResolutionAt) {
                <p class="sd-xs sd-muted">ETA {{ p.predictedResolutionAt | sdRelativeTime }}</p>
              }
              @if (p.lowConfidence) {
                <sd-badge size="sm" tone="warning" icon="triangle-alert">Low confidence</sd-badge>
              }
            } @else {
              <p class="sd-small sd-muted">{{ p.explanation }}</p>
            }
          } @else if (busy('prediction')) {
            <sd-skeleton [lines]="3" />
          } @else if (errors()['prediction']; as e) {
            <p class="sd-small sd-muted">{{ e }}</p>
          }
        </div>
      </section>

      <!-- Classification -->
      <section class="block" aria-labelledby="ai-cls">
        <header>
          <h3 id="ai-cls"><sd-icon name="tag" [size]="15" />Classification</h3>
          <button sdButton size="sm" variant="ghost" icon="wand" [loading]="busy('classify')" (click)="classify()">Suggest</button>
        </header>
        @if (classification(); as c) {
          <ul class="fields">
            @for (f of c.fields; track f.field) {
              @if (f.value) {
                <li>
                  @if (f.differs && canEdit()) {
                    <sd-checkbox [value]="selected().includes(f.field)" (valueChange)="toggleField(f.field, $event)" [ariaLabel]="'Apply ' + f.field" />
                  } @else {
                    <sd-icon name="check" [size]="14" class="same" aria-hidden="true" />
                  }
                  <span class="fname">{{ label(f.field) }}</span>
                  <span class="fval">
                    @if (f.differs) {
                      <span class="old">{{ f.currentLabel ?? 'empty' }}</span> → <strong>{{ f.label }}</strong>
                    } @else {
                      {{ f.label }}
                    }
                  </span>
                  <desk-ai-meter [value]="f.confidence" [label]="f.field + ' confidence'" />
                </li>
              }
            }
          </ul>
          @if (c.explanation) {
            <p class="sd-xs sd-muted">{{ c.explanation }}</p>
          }
          @if (canEdit() && hasDifferences()) {
            <button sdButton size="sm" variant="primary" icon="check" [disabled]="selected().length === 0" [loading]="busy('apply')" (click)="applyClassification()">
              Apply selected
            </button>
          }
        }
        @if (errors()['classify']; as e) {
          <p class="err" role="alert">{{ e }}</p>
        }
      </section>

      <!-- Suggested reply -->
      <section class="block" aria-labelledby="ai-reply">
        <header>
          <h3 id="ai-reply"><sd-icon name="send" [size]="15" />Suggested reply</h3>
          <button sdButton size="sm" variant="ghost" icon="sparkles" [loading]="busy('reply')" (click)="draftReply()">{{ reply() ? 'Redraft' : 'Draft reply' }}</button>
        </header>
        <label class="sd-visually-hidden" for="ai-instr">Instructions for the draft</label>
        <input id="ai-instr" class="sd-input sd-input-sm" [(ngModel)]="instructions" placeholder="Optional instructions, e.g. “mention the 8.0.1 upgrade”" />
        @if (reply(); as r) {
          <div class="draft">
            <div class="row">
              <sd-badge size="sm" [tone]="r.grounded ? 'success' : 'warning'" [icon]="r.grounded ? 'shield-check' : 'triangle-alert'">
                {{ r.grounded ? 'Grounded in knowledge' : 'Not grounded — review carefully' }}
              </sd-badge>
              <desk-ai-meter [value]="r.confidence" label="Draft confidence" />
            </div>
            <p class="text pre">{{ r.bodyText }}</p>
            @if (r.citations.length) {
              <ol class="cites" aria-label="Sources">
                @for (c of r.citations; track c.index) {
                  <li>
                    <span class="n">[{{ c.index }}]</span>
                    <a [routerLink]="citationLink(c.sourceType, c.sourceId)">{{ c.title }}</a>
                    <sd-badge size="sm" variant="outline">{{ c.sourceType === 'Ticket' ? 'Resolved ticket' : 'Article' }}</sd-badge>
                  </li>
                }
              </ol>
            }
            <div class="row">
              @if (canReply()) {
                <button sdButton size="sm" variant="primary" icon="arrow-down" (click)="insert(r)">Insert into composer</button>
              }
              <button sdButton size="sm" icon="copy" (click)="copy(r.bodyText)">Copy</button>
              <span class="spacer"></span>
              <button sdButton size="sm" variant="ghost" icon="thumbs-up" iconOnly aria-label="Helpful" sdTooltip="Helpful" (click)="feedback(r.executionId, true)"></button>
              <button sdButton size="sm" variant="ghost" icon="thumbs-down" iconOnly aria-label="Not helpful" sdTooltip="Not helpful" (click)="feedback(r.executionId, false)"></button>
            </div>
          </div>
        }
        @if (errors()['reply']; as e) {
          <p class="err" role="alert">{{ e }}</p>
        }
      </section>

      <!-- Similar & duplicates -->
      <section class="block" aria-labelledby="ai-sim">
        <header>
          <h3 id="ai-sim"><sd-icon name="layers" [size]="15" />Similar &amp; duplicates</h3>
          <button sdButton size="sm" variant="ghost" icon="search" [loading]="busy('similar')" (click)="findSimilar()">Find</button>
        </header>
        @if (duplicates(); as dups) {
          @if (dups.length) {
            <h4>Possible duplicates</h4>
            <ul class="items">
              @for (d of dups; track d.ticketId) {
                <li>
                  <div class="item-head">
                    <a [routerLink]="['/tickets', d.ticketId]">{{ d.displayNumber }} {{ d.subject }}</a>
                    <desk-ai-meter [value]="d.similarity" label="Similarity" />
                  </div>
                  <p class="sd-xs sd-muted">{{ d.reason }} · {{ d.status }}</p>
                  @if (d.canMerge && canMerge()) {
                    <button sdButton size="sm" icon="merge" [loading]="busy('merge')" (click)="merge(d)">Merge this ticket into {{ d.displayNumber }}</button>
                  }
                </li>
              }
            </ul>
          } @else {
            <p class="sd-small sd-muted">No open duplicates from this contact or customer.</p>
          }
        }
        @if (similar(); as sims) {
          @if (sims.length) {
            <h4>Similar resolved tickets</h4>
            <ul class="items">
              @for (s of sims; track s.ticketId) {
                <li>
                  <div class="item-head">
                    <a [routerLink]="['/tickets', s.ticketId]">{{ s.displayNumber }} {{ s.subject }}</a>
                    <desk-ai-meter [value]="s.similarity" label="Similarity" />
                  </div>
                  @if (s.resolutionSnippet) {
                    <p class="sd-small snippet">{{ s.resolutionSnippet }}</p>
                  }
                  @if (s.rootCause) {
                    <p class="sd-xs sd-muted">Root cause: {{ s.rootCause }}</p>
                  }
                </li>
              }
            </ul>
          } @else {
            <p class="sd-small sd-muted">No similar resolved tickets in the knowledge index.</p>
          }
        }
        @if (errors()['similar']; as e) {
          <p class="err" role="alert">{{ e }}</p>
        }
      </section>

      <!-- Root cause -->
      <section class="block" aria-labelledby="ai-rc">
        <header>
          <h3 id="ai-rc"><sd-icon name="lightbulb" [size]="15" />Likely root cause</h3>
          <button sdButton size="sm" variant="ghost" icon="sparkles" [loading]="busy('rootCause')" (click)="analyzeRootCause()">Analyse</button>
        </header>
        @if (rootCause(); as rc) {
          @if (rc.causes.length) {
            <ol class="items">
              @for (c of rc.causes; track c.cause) {
                <li>
                  <div class="item-head">
                    <strong>{{ c.cause }}</strong>
                    <desk-ai-meter [value]="c.likelihood" label="Likelihood" />
                  </div>
                  @if (c.explanation) {
                    <p class="sd-small snippet">{{ c.explanation }}</p>
                  }
                  @if (c.evidence.length) {
                    <p class="sd-xs evidence">
                      Evidence:
                      @for (e of c.evidence; track e.ticketId) {
                        <a [routerLink]="['/tickets', e.ticketId]" [sdTooltip]="e.subject">#{{ e.number }}</a>
                      }
                    </p>
                  }
                </li>
              }
            </ol>
          } @else {
            <p class="sd-small sd-muted">Not enough similar resolved tickets with a recorded root cause.</p>
          }
          <p class="sd-xs sd-muted">{{ rc.sampleSize }} ticket(s) considered · {{ rc.provider }}</p>
        }
        @if (errors()['rootCause']; as e) {
          <p class="err" role="alert">{{ e }}</p>
        }
      </section>

      <!-- Knowledge draft -->
      @if (canAuthor()) {
        <section class="block" aria-labelledby="ai-kb">
          <header>
            <h3 id="ai-kb"><sd-icon name="book-open" [size]="15" />Knowledge article</h3>
            <button sdButton size="sm" variant="ghost" icon="file-plus" [loading]="busy('draft')" (click)="draftArticle()">Draft article</button>
          </header>
          @if (draft(); as d) {
            <p class="sd-small" [class.ok]="d.created">{{ d.message }}</p>
            @if (d.created && d.articleId) {
              <a [routerLink]="['/kb/articles', d.articleId]">Open draft “{{ d.title }}”</a>
            } @else {
              <h4>{{ d.title }}</h4>
              <div class="sd-prose preview" [innerHTML]="d.bodyHtml"></div>
              <button sdButton size="sm" icon="copy" (click)="copy(d.bodyHtml)">Copy HTML</button>
            }
          }
          @if (errors()['draft']; as e) {
            <p class="err" role="alert">{{ e }}</p>
          }
        </section>
      }

      <!-- Proposals -->
      <section class="block" aria-labelledby="ai-prop">
        <header>
          <h3 id="ai-prop"><sd-icon name="list-checks" [size]="15" />AI proposals</h3>
          @if (canRunAgents()) {
            <button sdButton size="sm" variant="ghost" icon="bot" [loading]="busy('agents')" (click)="runAgents()">Run agents</button>
          }
        </header>
        @if (agentNotes().length) {
          <ul class="notes sd-xs sd-muted">
            @for (n of agentNotes(); track n) {
              <li>{{ n }}</li>
            }
          </ul>
        }
        @if (proposals().length) {
          <div class="proposals">
            @for (p of proposals(); track p.id) {
              <desk-ai-proposal-card [proposal]="p" [showTicket]="false" (changed)="onProposalChanged($event)" />
            }
          </div>
        } @else if (!busy('proposals')) {
          <p class="sd-small sd-muted">No AI proposals for this ticket.</p>
        }
      </section>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .panel {
      display: grid;
      gap: 12px;
    }
    .block {
      display: grid;
      gap: 8px;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      min-width: 0;
    }
    .block.two {
      grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
      gap: 16px;
    }
    .block.two > div {
      display: grid;
      gap: 6px;
      align-content: start;
    }
    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
    }
    h3 {
      display: flex;
      align-items: center;
      gap: 6px;
      margin: 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    h4 {
      margin: 4px 0 0;
      font-size: var(--sd-text-xs);
      color: var(--sd-text-3);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .text {
      font-size: var(--sd-text-sm);
    }
    .pre {
      white-space: pre-line;
    }
    .status {
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .actions-list {
      margin: 0;
      padding-left: 18px;
      font-size: var(--sd-text-sm);
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
    }
    .spacer {
      flex: 1;
    }
    .big {
      font-size: var(--sd-text-xl);
      font-weight: var(--sd-weight-semibold);
    }
    .dots {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }
    .dot {
      width: 10px;
      height: 10px;
      border-radius: 50%;
      background: var(--sd-surface-3);
    }
    .dot[data-s='Positive'] {
      background: var(--sd-success);
    }
    .dot[data-s='Neutral'] {
      background: var(--sd-info);
    }
    .dot[data-s='Negative'] {
      background: var(--sd-warning);
    }
    .dot[data-s='Frustrated'] {
      background: var(--sd-danger);
    }
    .warn {
      color: var(--sd-danger);
    }
    .fields,
    .items,
    .cites,
    .notes {
      display: grid;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .fields li {
      display: grid;
      grid-template-columns: 20px 90px minmax(0, 1fr) 110px;
      gap: 8px;
      align-items: center;
      font-size: var(--sd-text-sm);
    }
    .fname {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      text-transform: uppercase;
    }
    .fval {
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .old {
      color: var(--sd-text-3);
      text-decoration: line-through;
    }
    .same {
      color: var(--sd-success);
    }
    .draft {
      display: grid;
      gap: 8px;
      padding: 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    .cites li {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
      font-size: var(--sd-text-sm);
    }
    .n {
      color: var(--sd-text-3);
      font-variant-numeric: tabular-nums;
    }
    .item-head {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      align-items: center;
      font-size: var(--sd-text-sm);
    }
    .item-head a,
    .item-head strong {
      min-width: 0;
      overflow-wrap: anywhere;
    }
    .snippet {
      color: var(--sd-text-2);
    }
    .evidence a {
      margin-left: 6px;
    }
    .preview {
      max-height: 240px;
      overflow: auto;
      padding: 8px;
      border: 1px dashed var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    .ok {
      color: var(--sd-success);
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .proposals {
      display: grid;
      gap: 8px;
    }
  `,
})
export class TicketAiPanelComponent {
  private readonly ai = inject(AiApi);
  private readonly tickets = inject(TicketsApi);
  private readonly me = inject(MeStore);
  private readonly dialogs = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly ticketId = input.required<string>();
  /** The reply draft as HTML for the composer. */
  readonly insertReply = output<string>();
  /** The ticket was changed by an AI action (classification, merge, applied proposal) — reload it. */
  readonly ticketChanged = output<void>();

  protected readonly ticket = signal<TicketDetailDto | null>(null);
  protected readonly summary = signal<TicketSummaryDto | null>(null);
  protected readonly sentiment = signal<SentimentDto | null>(null);
  protected readonly prediction = signal<ResolutionPredictionDto | null>(null);
  protected readonly classification = signal<ClassificationSuggestionDto | null>(null);
  protected readonly selected = signal<string[]>([]);
  protected readonly reply = signal<SuggestedReplyDto | null>(null);
  protected readonly similar = signal<SimilarTicketDto[] | null>(null);
  protected readonly duplicates = signal<DuplicateTicketCandidateDto[] | null>(null);
  protected readonly rootCause = signal<RootCauseDto | null>(null);
  protected readonly draft = signal<KnowledgeDraftDto | null>(null);
  protected readonly proposals = signal<AiProposalDto[]>([]);
  protected readonly agentNotes = signal<string[]>([]);
  protected readonly errors = signal<Partial<Record<Section | 'apply' | 'merge', string>>>({});
  private readonly loading = signal<ReadonlySet<string>>(new Set());
  protected instructions = '';

  protected readonly canEdit = computed(() => this.me.can('tickets.edit'));
  protected readonly canReply = computed(() => this.me.can('tickets.reply'));
  protected readonly canMerge = computed(() => this.me.can('tickets.merge'));
  protected readonly canAuthor = computed(() => this.me.can('kb.author'));
  protected readonly canRunAgents = computed(() => this.me.can('ai.use') && this.me.can('tickets.edit'));
  protected readonly hasDifferences = computed(() => (this.classification()?.fields ?? []).some((f) => f.differs && f.value));
  protected readonly label = humanize;
  protected readonly tone = sentimentTone;
  protected readonly fmtHours = hours;
  protected readonly pct = pct;

  constructor() {
    effect(() => {
      const id = this.ticketId();
      untracked(() => this.reset(id));
    });
  }

  protected busy(section: string): boolean {
    return this.loading().has(section);
  }

  private reset(id: string): void {
    this.summary.set(null);
    this.classification.set(null);
    this.reply.set(null);
    this.similar.set(null);
    this.duplicates.set(null);
    this.rootCause.set(null);
    this.draft.set(null);
    this.agentNotes.set([]);
    this.errors.set({});
    this.tickets
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (t) => this.ticket.set(t), error: () => this.ticket.set(null) });
    this.call('sentiment', this.ai.sentiment(id, { silent: true }), (s) => this.sentiment.set(s));
    if (this.me.can('tickets.view')) this.call('prediction', this.ai.resolutionPrediction(id, { silent: true }), (p) => this.prediction.set(p));
    this.loadProposals();
  }

  /** Runs a request with per-section busy/error state. */
  private call<T>(section: Section | 'apply' | 'merge', source: Observable<T>, next: (value: T) => void): void {
    this.loading.update((s) => new Set(s).add(section));
    this.errors.update((e) => ({ ...e, [section]: undefined }));
    source.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (value) => {
        next(value);
        this.done(section);
      },
      error: (err: unknown) => {
        this.errors.update((e) => ({ ...e, [section]: aiErrorMessage(err) }));
        this.done(section);
      },
    });
  }

  private done(section: string): void {
    this.loading.update((s) => {
      const next = new Set(s);
      next.delete(section);
      return next;
    });
  }

  protected summarize(): void {
    this.call('summary', this.ai.summarize(this.ticketId(), { silent: true }), (s) => this.summary.set(s));
  }

  protected analyzeSentiment(): void {
    this.call('sentiment', this.ai.analyzeSentiment(this.ticketId()), (s) => this.sentiment.set(s));
  }

  protected classify(): void {
    this.call('classify', this.ai.classify(this.ticketId(), { silent: true }), (c) => {
      this.classification.set(c);
      this.selected.set(c.fields.filter((f) => f.differs && f.value && f.field !== 'department').map((f) => f.field));
    });
  }

  protected toggleField(field: string, on: boolean): void {
    this.selected.update((s) => (on ? [...new Set([...s, field])] : s.filter((x) => x !== field)));
  }

  protected applyClassification(): void {
    const c = this.classification();
    if (!c) return;
    this.call('apply', this.ai.applyClassification(this.ticketId(), { executionId: c.executionId, fields: this.selected() }), (r) => {
      this.toast.success('Classification applied', r.message);
      this.classification.set(null);
      this.ticketChanged.emit();
      this.tickets.get(this.ticketId()).subscribe((t) => this.ticket.set(t));
    });
  }

  protected draftReply(): void {
    this.call('reply', this.ai.suggestReply(this.ticketId(), { instructions: this.instructions.trim() || null }, { silent: true }), (r) => this.reply.set(r));
  }

  protected insert(r: SuggestedReplyDto): void {
    this.insertReply.emit(r.bodyHtml);
    this.ai.feedback(r.executionId, true).subscribe({ error: () => undefined });
    this.toast.info('Draft inserted', 'Review it and remove the [n] source markers before sending.');
  }

  protected feedback(executionId: string, accepted: boolean): void {
    this.ai.feedback(executionId, accepted).subscribe({ next: () => this.toast.info('Thanks for the feedback'), error: () => undefined });
  }

  protected async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.toast.success('Copied to the clipboard');
    } catch {
      this.toast.warning('Copy failed', 'Your browser blocked clipboard access.');
    }
  }

  protected citationLink(sourceType: string, id: string): unknown[] {
    return sourceType === 'Ticket' ? ['/tickets', id] : ['/kb/articles', id];
  }

  protected findSimilar(): void {
    this.call('similar', forkJoin({ similar: this.ai.similar(this.ticketId(), { silent: true }), dups: this.ai.duplicates(this.ticketId(), { silent: true }) }), (r) => {
      this.similar.set(r.similar);
      this.duplicates.set(r.dups.candidates);
    });
  }

  protected async merge(d: DuplicateTicketCandidateDto): Promise<void> {
    const t = this.ticket();
    const ok = await this.dialogs.confirm({
      title: `Merge ${t?.displayNumber ?? 'this ticket'} into ${d.displayNumber}?`,
      message: `The conversation, attachments and watchers move to ${d.displayNumber}; this ticket is closed as a duplicate.`,
      confirmLabel: 'Merge',
      danger: true,
    });
    if (!ok) return;
    this.call('merge', this.ai.mergeDuplicate(this.ticketId(), { targetTicketId: d.ticketId }), () => {
      this.toast.success(`Merged into ${d.displayNumber}`);
      this.ticketChanged.emit();
      this.duplicates.update((list) => (list ?? []).filter((x) => x.ticketId !== d.ticketId));
    });
  }

  protected analyzeRootCause(): void {
    this.call('rootCause', this.ai.rootCause(this.ticketId(), { silent: true }), (r) => this.rootCause.set(r));
  }

  protected draftArticle(): void {
    this.call('draft', this.ai.knowledgeDraft(this.ticketId(), { silent: true }), (d) => this.draft.set(d));
  }

  private loadProposals(): void {
    this.call('proposals', this.ai.ticketProposals(this.ticketId(), null, { silent: true }), (p) => this.proposals.set(p));
  }

  protected runAgents(): void {
    this.call('agents', this.ai.runAgents(this.ticketId()), (r) => {
      this.agentNotes.set(r.notes);
      this.toast.info('AI agents finished', `${r.agentsRun} agent(s) ran · ${r.proposalsCreated} new proposal(s)`);
      this.loadProposals();
    });
  }

  protected onProposalChanged(p: AiProposalDto): void {
    this.proposals.update((list) => list.map((x) => (x.id === p.id ? p : x)));
    if (p.status === 'Applied' || p.status === 'Reverted') this.ticketChanged.emit();
    if (p.error) this.loadProposals();
  }
}
