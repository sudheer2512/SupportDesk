import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { FlowNodeDto, FlowOptionDto, PortalArticleSummaryDto, PortalAuthService, PortalContentApi, PortalFlowDto } from '@sd/core';
import { SdButton, SdIcon, SdSkeleton } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { ErrorStateComponent } from '../../shared/error-state.component';
import { escapeHtml, htmlToText } from '../../shared/portal-utils';
import { ProseLinksDirective } from '../../shared/prose-links.directive';
import { SubmitPrefillService } from '../../shared/submit-prefill.service';

/** One visited node and the answer that left it (null while it is the current step). */
interface TrailEntry {
  nodeId: string;
  choice: string | null;
}

type Outcome = 'resolved' | 'escalated' | null;

const NOT_SOLVED = "This didn't solve it";

/**
 * Guided troubleshooting runner: walks the flow's decision tree (question → options, solution / article
 * → "did this solve it?", escalate → create a prefilled ticket), with back / start-over and a breadcrumb
 * of answers. Posts usage (started / resolved / escalated with the path) for the flow analytics.
 */
@Component({
  selector: 'portal-flow-runner-page',
  imports: [RouterLink, SdButton, SdIcon, SdSkeleton, BreadcrumbComponent, ErrorStateComponent, ProseLinksDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page narrow">
      <portal-breadcrumb
        [items]="[{ label: 'Home', link: brand.base() }, { label: 'Help & contact', link: brand.base() + '/help' }, { label: flow()?.name ?? 'Troubleshooting' }]"
      />
      @if (res.error()) {
        <portal-error-state
          [error]="res.error()"
          notFoundTitle="Troubleshooter not found"
          notFoundMessage="This guide is no longer available. Try another one or contact us."
          (retry)="res.reload()"
        >
          <a sdButton [routerLink]="brand.base() + '/help'">All troubleshooting guides</a>
        </portal-error-state>
      } @else if (flow(); as f) {
        <header class="portal-page-head">
          <div>
            <p class="eyebrow"><sd-icon name="route" [size]="14" />Guided troubleshooting{{ f.productName ? ' · ' + f.productName : '' }}</p>
            <h1>{{ f.name }}</h1>
            @if (f.description) {
              <p class="portal-sub">{{ f.description }}</p>
            }
          </div>
        </header>

        @if (answered().length) {
          <nav class="trail" aria-label="Your answers so far">
            <ol>
              @for (a of answered(); track $index) {
                <li>
                  <span class="trail-q">{{ a.question }}</span>
                  <span class="trail-a"><sd-icon name="arrow-right" [size]="12" />{{ a.answer }}</span>
                </li>
              }
            </ol>
          </nav>
        }

        <section class="step" [attr.aria-label]="'Step ' + stepNumber()">
          @if (outcome() === 'resolved') {
            <div class="result success">
              <span class="result-ic" aria-hidden="true"><sd-icon name="circle-check" [size]="28" /></span>
              <h2 #stepHeading tabindex="-1">Great — glad that fixed it!</h2>
              <p>Thanks for letting us know. You can close this page or explore more help.</p>
              <div class="actions">
                <a sdButton variant="primary" [routerLink]="brand.base()">Back to the help center</a>
                <button sdButton type="button" icon="history" (click)="restart()">Start over</button>
              </div>
            </div>
          } @else if (outcome() === 'escalated') {
            <div class="result escalate">
              <span class="result-ic" aria-hidden="true"><sd-icon name="headphones" [size]="28" /></span>
              <h2 #stepHeading tabindex="-1">Let's get our team on it</h2>
              @if (current()?.type === 'escalate' && current()?.text) {
                <div class="sd-prose portal-prose portal-prose--compact" [innerHTML]="current()!.text" portalProseLinks></div>
              } @else {
                <p>We couldn't solve this together. Create a ticket and we'll include the steps you've already tried.</p>
              }
              <div class="actions">
                <button sdButton variant="primary" type="button" icon="file-plus" (click)="createTicket(f)">Create a ticket</button>
                <a sdButton variant="ghost" icon="life-buoy" [routerLink]="brand.base() + '/help'">Other ways to contact us</a>
              </div>
            </div>
          } @else if (current(); as node) {
            <p class="step-no">Step {{ stepNumber() }}</p>
            @switch (node.type) {
              @case ('question') {
                <h2 #stepHeading tabindex="-1" class="question" [id]="'q-' + node.id">{{ plain(node.text) || node.title }}</h2>
                <div class="options" role="group" [attr.aria-labelledby]="'q-' + node.id">
                  @for (o of node.options; track $index) {
                    <button type="button" class="option" (click)="choose(o)">
                      <span>{{ o.label }}</span><sd-icon name="chevron-right" [size]="16" />
                    </button>
                  }
                  @if (!node.options.length) {
                    <button sdButton type="button" (click)="escalate()">None of these — contact support</button>
                  }
                </div>
              }
              @case ('escalate') {
                <h2 #stepHeading tabindex="-1">{{ node.title || "Let's get our team on it" }}</h2>
              }
              @default {
                <h2 #stepHeading tabindex="-1">{{ node.title || (node.type === 'article' ? 'This article should help' : 'Try this') }}</h2>
                @if (node.type === 'article' && articleFor(node); as article) {
                  <div class="article">
                    <sd-icon name="file-text" [size]="18" />
                    <div>
                      <p class="article-title">{{ article.title }}</p>
                      @if (article.summary) {
                        <p class="article-summary">{{ article.summary }}</p>
                      }
                      <a [routerLink]="[brand.base(), 'kb', article.slug]" target="_blank" rel="noopener" class="article-link">
                        Read the full article<span class="sd-visually-hidden"> (opens in a new tab)</span>
                        <sd-icon name="external-link" [size]="13" />
                      </a>
                    </div>
                  </div>
                }
                @if (node.text) {
                  <div class="sd-prose portal-prose portal-prose--compact solution" [innerHTML]="node.text" portalProseLinks></div>
                }
                <div class="solved" role="group" aria-labelledby="solved-q">
                  <p id="solved-q">Did this solve your problem?</p>
                  <button sdButton variant="primary" type="button" icon="thumbs-up" (click)="solved(true)">Yes, it's fixed</button>
                  <button sdButton type="button" icon="thumbs-down" (click)="solved(false)">No</button>
                </div>
              }
            }
          }
        </section>

        <div class="nav">
          <button sdButton variant="ghost" type="button" icon="arrow-left" [disabled]="!canGoBack()" (click)="back()">Back</button>
          <button sdButton variant="ghost" type="button" icon="history" [disabled]="!canGoBack()" (click)="restart()">Start over</button>
        </div>
      } @else {
        <div class="loading" aria-busy="true" aria-label="Loading troubleshooter">
          <sd-skeleton height="34px" width="60%" />
          <sd-skeleton [lines]="2" />
          <sd-skeleton height="220px" radius="var(--sd-radius-lg)" />
        </div>
      }
    </div>
  `,
  styles: `
    .narrow {
      max-width: 820px;
    }
    .eyebrow {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      margin: 0 0 6px;
      color: var(--sd-accent);
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .trail ol {
      display: grid;
      gap: 6px;
      margin: 0 0 16px;
      padding: 0;
      list-style: none;
      counter-reset: step;
    }
    .trail li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 10px;
      padding: 8px 12px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      font-size: var(--sd-text-sm);
      counter-increment: step;
    }
    .trail li::before {
      content: counter(step);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 20px;
      height: 20px;
      border-radius: 50%;
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      font-size: 11px;
      font-weight: var(--sd-weight-semibold);
    }
    .trail-q {
      color: var(--sd-text-2);
    }
    .trail-a {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-weight: var(--sd-weight-medium);
    }
    .step {
      padding: clamp(18px, 4vw, 28px);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-sm);
    }
    .step-no {
      margin: 0 0 6px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    h2 {
      font-size: var(--sd-text-xl);
      outline: none;
    }
    .options {
      display: grid;
      gap: 8px;
      margin-top: 18px;
    }
    .option {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      min-height: 48px;
      padding: 10px 16px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font: inherit;
      font-weight: var(--sd-weight-medium);
      text-align: left;
      cursor: pointer;
      transition: border-color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .option:hover {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
    }
    .option:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .option sd-icon {
      color: var(--sd-text-3);
    }
    .article {
      display: flex;
      gap: 12px;
      margin-top: 14px;
      padding: 14px;
      border: 1px solid var(--sd-primary-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
    }
    .article > sd-icon {
      color: var(--sd-primary);
      margin-top: 2px;
    }
    .article-title {
      margin: 0;
      font-weight: var(--sd-weight-semibold);
    }
    .article-summary {
      margin: 4px 0 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
    }
    .article-link {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      margin-top: 8px;
      font-weight: var(--sd-weight-medium);
    }
    .solution {
      margin-top: 14px;
    }
    .solved {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 10px;
      margin-top: 22px;
      padding-top: 18px;
      border-top: 1px solid var(--sd-divider);
    }
    .solved p {
      margin: 0 auto 0 0;
      font-weight: var(--sd-weight-semibold);
    }
    .result {
      display: grid;
      justify-items: center;
      gap: 8px;
      text-align: center;
    }
    .result p {
      max-width: 520px;
      margin: 0;
      color: var(--sd-text-2);
    }
    .result .sd-prose {
      max-width: 560px;
      text-align: left;
    }
    .result-ic {
      display: inline-flex;
      padding: 12px;
      border-radius: 50%;
    }
    .success .result-ic {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .escalate .result-ic {
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 10px;
      margin-top: 10px;
    }
    .nav {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      margin-top: 12px;
    }
    .loading {
      display: grid;
      gap: 16px;
      margin-top: 16px;
    }
  `,
})
export class FlowRunnerPage {
  readonly id = input.required<string>();

  protected readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);
  private readonly api = inject(PortalContentApi);
  private readonly router = inject(Router);
  private readonly prefill = inject(SubmitPrefillService);
  private readonly injector = inject(Injector);
  private readonly stepHeading = viewChild<ElementRef<HTMLElement>>('stepHeading');

  protected readonly res = rxResource({
    params: () => {
      const brand = this.brand.slug();
      return brand ? { brand, id: this.id(), signedIn: this.auth.isAuthenticated() } : undefined;
    },
    stream: ({ params }) => this.api.flow(params.brand, params.id),
  });
  protected readonly flow = computed<PortalFlowDto | null>(() => (this.res.hasValue() ? this.res.value() : null));
  private readonly nodes = computed(() => new Map((this.flow()?.definition.nodes ?? []).map((n) => [n.id, n] as const)));

  protected readonly trail = signal<TrailEntry[]>([]);
  private readonly _outcome = signal<Outcome>(null);
  protected readonly current = computed<FlowNodeDto | null>(() => {
    const last = this.trail().at(-1);
    return last ? (this.nodes().get(last.nodeId) ?? null) : null;
  });
  /** Arriving at an escalate node (or a dangling link) counts as escalation. */
  protected readonly outcome = computed<Outcome>(() => {
    const explicit = this._outcome();
    if (explicit) return explicit;
    if (this.trail().length && (!this.current() || this.current()?.type === 'escalate')) return 'escalated';
    return null;
  });
  protected readonly stepNumber = computed(() => Math.max(1, this.trail().length));
  protected readonly canGoBack = computed(() => this.trail().length > 1 || this._outcome() !== null);
  protected readonly answered = computed(() =>
    this.trail()
      .filter((e) => e.choice !== null)
      .map((e) => ({ question: this.describe(this.nodes().get(e.nodeId)), answer: e.choice as string })),
  );

  private startedFor: string | null = null;
  private reported = new Set<string>();

  constructor() {
    effect(() => {
      const f = this.flow();
      if (!f) return;
      untracked(() => {
        this.brand.setPageTitle(f.name);
        if (this.startedFor !== f.id) {
          this.startedFor = f.id;
          this.reported.clear();
          this.trail.set([{ nodeId: f.definition.startNodeId, choice: null }]);
          this._outcome.set(null);
          this.report('started');
        }
      });
    });
    // Escalation reached by navigation (escalate node) is reported once per run.
    effect(() => {
      if (this.outcome() === 'escalated') untracked(() => this.report('escalated'));
    });
  }

  protected plain(html: string | null | undefined): string {
    return htmlToText(html);
  }

  protected articleFor(node: FlowNodeDto): PortalArticleSummaryDto | null {
    return this.flow()?.articles.find((a) => a.id === node.articleId) ?? null;
  }

  protected choose(option: FlowOptionDto): void {
    this.advance(option.label, option.next);
  }

  protected solved(yes: boolean): void {
    const node = this.current();
    if (!node) return;
    if (yes) {
      this._outcome.set('resolved');
      this.report('resolved');
      this.focusStep();
      return;
    }
    const next = node.options[0];
    if (next) this.advance(NOT_SOLVED, next.next);
    else this.escalate(NOT_SOLVED);
  }

  protected escalate(choice: string | null = null): void {
    if (choice) this.trail.update((t) => t.map((e, i) => (i === t.length - 1 ? { ...e, choice } : e)));
    this._outcome.set('escalated');
    this.focusStep();
  }

  protected back(): void {
    if (this._outcome() !== null && this.current()?.type !== 'escalate') {
      this._outcome.set(null);
      this.trail.update((t) => t.map((e, i) => (i === t.length - 1 ? { ...e, choice: null } : e)));
    } else if (this.trail().length > 1) {
      this._outcome.set(null);
      this.trail.update((t) => {
        const copy = t.slice(0, -1);
        copy[copy.length - 1] = { ...copy[copy.length - 1], choice: null };
        return copy;
      });
    }
    this.focusStep();
  }

  protected restart(): void {
    const f = this.flow();
    if (!f) return;
    this._outcome.set(null);
    this.reported.delete('resolved');
    this.reported.delete('escalated');
    this.trail.set([{ nodeId: f.definition.startNodeId, choice: null }]);
    this.focusStep();
  }

  protected createTicket(f: PortalFlowDto): void {
    const steps = this.answered().map((a) => `<li>Q: ${escapeHtml(a.question)} → A: ${escapeHtml(a.answer)}</li>`);
    const intro = `<p>I followed the “${escapeHtml(f.name)}” troubleshooting guide${steps.length ? ':' : ' but it did not solve my problem.'}</p>`;
    this.prefill.set({
      subject: f.name,
      descriptionHtml: `${intro}${steps.length ? `<ul>${steps.join('')}</ul>` : ''}<p></p>`,
      sourceFlowId: f.id,
    });
    void this.router.navigate([this.brand.base(), 'submit']);
  }

  private advance(choice: string, nextId: string): void {
    this.trail.update((t) => [...t.map((e, i) => (i === t.length - 1 ? { ...e, choice } : e)), { nodeId: nextId, choice: null }]);
    this._outcome.set(null);
    this.focusStep();
  }

  private describe(node: FlowNodeDto | undefined): string {
    if (!node) return 'Step';
    if (node.type === 'question') return truncate(htmlToText(node.text) || node.title || 'Question');
    if (node.type === 'article') return truncate(node.title || this.articleFor(node)?.title || 'Suggested article');
    return truncate(node.title || htmlToText(node.text) || 'Suggested fix');
  }

  private report(outcome: 'started' | 'resolved' | 'escalated'): void {
    const f = this.flow();
    const slug = this.brand.slug();
    if (!f || !slug || this.reported.has(outcome)) return;
    this.reported.add(outcome);
    const path = this.trail().map((e) => e.nodeId);
    this.api.flowUsage(slug, f.id, { outcome, path }).subscribe({ error: () => undefined });
  }

  private focusStep(): void {
    afterNextRender(() => this.stepHeading()?.nativeElement.focus({ preventScroll: false }), { injector: this.injector });
  }
}

function truncate(text: string, max = 140): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}
