import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, effect, inject, signal, viewChild } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { PortalCitationDto } from '@sd/core';
import { SdButton, SdIcon } from '@sd/ui';
import { BrandService } from '../brand.service';
import { escapeHtml } from '../shared/portal-utils';
import { SubmitPrefillService } from '../shared/submit-prefill.service';
import { AssistantService } from './assistant.service';

/**
 * Chat panel for the help assistant: plain-text answers (line breaks kept) with numbered citations that
 * link to knowledge-base articles, and a "Create a ticket" shortcut when the assistant suggests it.
 * Enter sends, Shift+Enter adds a line.
 */
@Component({
  selector: 'portal-assistant-panel',
  imports: [RouterLink, SdButton, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel">
      <div #log class="log" role="log" aria-live="polite" aria-relevant="additions" aria-label="Conversation with the assistant">
        <div class="turn bot intro">
          <span class="bot-ic" aria-hidden="true"><sd-icon name="sparkles" [size]="16" /></span>
          <div class="bubble">
            Hi! I'm the {{ brand.name() }} assistant. Ask me a question and I'll answer from our help articles.
          </div>
        </div>
        @for (turn of assistant.turns(); track turn.id) {
          @switch (turn.role) {
            @case ('user') {
              <div class="turn me">
                <div class="bubble"><span class="sd-visually-hidden">You: </span>{{ turn.text }}</div>
              </div>
            }
            @case ('assistant') {
              <div class="turn bot">
                <span class="bot-ic" aria-hidden="true"><sd-icon name="sparkles" [size]="16" /></span>
                <div class="bubble">
                  <span class="sd-visually-hidden">Assistant: </span>
                  <p class="text">{{ turn.text }}</p>
                  @if (turn.reply?.citations?.length) {
                    <div class="sources">
                      <p class="sources-title">Sources</p>
                      <ol>
                        @for (c of turn.reply!.citations; track c.index) {
                          <li>
                            <span class="idx">{{ c.index }}</span>
                            @if (isPortalLink(c)) {
                              <a [routerLink]="c.url!">{{ c.title }}</a>
                            } @else if (c.url) {
                              <a [href]="c.url" target="_blank" rel="noopener noreferrer">{{ c.title }}<span class="sd-visually-hidden"> (opens in a new tab)</span></a>
                            } @else {
                              <span>{{ c.title }}</span>
                            }
                          </li>
                        }
                      </ol>
                    </div>
                  }
                  @if (turn.reply?.suggestTicket) {
                    <div class="suggest">
                      <p>Would you like our team to look into this?</p>
                      <button sdButton size="sm" variant="primary" type="button" icon="file-plus" (click)="createTicket()">Create a ticket</button>
                    </div>
                  }
                </div>
              </div>
            }
            @case ('error') {
              <div class="turn bot">
                <span class="bot-ic err" aria-hidden="true"><sd-icon name="circle-alert" [size]="16" /></span>
                <div class="bubble error" role="alert">
                  {{ turn.text }}
                  <button sdButton size="sm" variant="link" type="button" (click)="createTicket()">Submit a ticket instead</button>
                </div>
              </div>
            }
          }
        }
        @if (assistant.busy()) {
          <div class="turn bot">
            <span class="sd-visually-hidden">The assistant is typing…</span>
            <span class="bot-ic" aria-hidden="true"><sd-icon name="sparkles" [size]="16" /></span>
            <div class="bubble typing" aria-hidden="true"><span></span><span></span><span></span></div>
          </div>
        }
      </div>

      <form class="compose" (submit)="$event.preventDefault(); send()">
        <label for="assistant-input" class="sd-visually-hidden">Your question</label>
        <textarea
          #input
          id="assistant-input"
          rows="2"
          maxlength="2000"
          placeholder="Ask a question…"
          [value]="draft()"
          (input)="draft.set($any($event.target).value)"
          (keydown.enter)="onEnter($event)"
        ></textarea>
        <button sdButton variant="primary" type="submit" icon="send" iconOnly aria-label="Send question" [loading]="assistant.busy()" [disabled]="!draft().trim()"></button>
      </form>
      <p class="foot">
        Answers are generated from our help content and may not be perfect.
        @if (assistant.turns().length) {
          <button sdButton size="sm" variant="link" type="button" (click)="assistant.clear()">Clear conversation</button>
        }
      </p>
    </div>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }
    .panel {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 360px;
      max-height: 100%;
    }
    .log {
      flex: 1;
      display: flex;
      flex-direction: column;
      gap: 12px;
      min-height: 0;
      overflow-y: auto;
      padding: 4px 2px 12px;
    }
    .turn {
      display: flex;
      gap: 8px;
      max-width: 100%;
    }
    .turn.me {
      justify-content: flex-end;
    }
    .bot-ic {
      display: inline-flex;
      flex: none;
      align-items: center;
      justify-content: center;
      width: 28px;
      height: 28px;
      border-radius: 50%;
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
    }
    .bot-ic.err {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .bubble {
      max-width: 86%;
      padding: 10px 12px;
      border: 1px solid var(--sd-border);
      border-radius: 4px var(--sd-radius-lg) var(--sd-radius-lg) var(--sd-radius-lg);
      background: var(--sd-surface);
      font-size: var(--sd-text-sm);
      line-height: 1.55;
      overflow-wrap: anywhere;
    }
    .me .bubble {
      border-color: transparent;
      border-radius: var(--sd-radius-lg) 4px var(--sd-radius-lg) var(--sd-radius-lg);
      background: var(--sd-primary);
      color: var(--sd-on-primary);
      white-space: pre-line;
    }
    .bubble.error {
      border-color: var(--sd-danger-border);
      background: var(--sd-danger-bg);
    }
    .text {
      margin: 0;
      white-space: pre-line;
    }
    .sources {
      margin-top: 10px;
      padding-top: 8px;
      border-top: 1px solid var(--sd-divider);
    }
    .sources-title {
      margin: 0 0 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .sources ol {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .sources li {
      display: flex;
      align-items: baseline;
      gap: 6px;
    }
    .idx {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      flex: none;
      min-width: 18px;
      height: 18px;
      border-radius: var(--sd-radius-xs);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: 11px;
      font-weight: var(--sd-weight-semibold);
    }
    .suggest {
      display: grid;
      gap: 6px;
      margin-top: 10px;
      padding: 10px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
    }
    .suggest p {
      margin: 0;
    }
    .typing {
      display: inline-flex;
      gap: 4px;
      align-items: center;
    }
    .typing span {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: var(--sd-text-3);
      animation: portal-bounce 1.2s infinite ease-in-out;
    }
    .typing span:nth-child(2) {
      animation-delay: 0.15s;
    }
    .typing span:nth-child(3) {
      animation-delay: 0.3s;
    }
    @keyframes portal-bounce {
      0%,
      80%,
      100% {
        opacity: 0.3;
        transform: translateY(0);
      }
      40% {
        opacity: 1;
        transform: translateY(-3px);
      }
    }
    .compose {
      display: flex;
      align-items: flex-end;
      gap: 8px;
      padding-top: 10px;
      border-top: 1px solid var(--sd-divider);
    }
    textarea {
      flex: 1;
      min-width: 0;
      min-height: 44px;
      max-height: 140px;
      padding: 10px 12px;
      border: 1px solid var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font: inherit;
      resize: vertical;
    }
    textarea:focus {
      outline: none;
      border-color: var(--sd-primary);
      box-shadow: 0 0 0 3px var(--sd-focus);
    }
    .foot {
      margin: 8px 0 0;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    @media (max-width: 640px) {
      .panel {
        height: 70dvh;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .typing span {
        animation: none;
      }
    }
  `,
})
export class AssistantPanelComponent {
  protected readonly assistant = inject(AssistantService);
  protected readonly brand = inject(BrandService);
  private readonly router = inject(Router);
  private readonly prefill = inject(SubmitPrefillService);
  private readonly injector = inject(Injector);
  private readonly log = viewChild.required<ElementRef<HTMLElement>>('log');
  private readonly input = viewChild.required<ElementRef<HTMLTextAreaElement>>('input');

  protected readonly draft = signal('');

  constructor() {
    afterNextRender(() => this.input().nativeElement.focus());
    effect(() => {
      this.assistant.turns();
      this.assistant.busy();
      afterNextRender(
        () => {
          const el = this.log().nativeElement;
          el.scrollTop = el.scrollHeight;
        },
        { injector: this.injector },
      );
    });
  }

  protected isPortalLink(c: PortalCitationDto): boolean {
    return !!c.url && c.url.startsWith('/') && !c.url.startsWith('//');
  }

  protected onEnter(event: Event): void {
    const e = event as KeyboardEvent;
    if (e.shiftKey || e.isComposing) return;
    e.preventDefault();
    this.send();
  }

  protected send(): void {
    const text = this.draft().trim();
    if (!text || this.assistant.busy()) return;
    this.draft.set('');
    void this.assistant.ask(text);
  }

  protected createTicket(): void {
    const question = this.assistant.lastQuestion();
    this.prefill.set({
      subject: question ? question.slice(0, 150) : null,
      descriptionHtml: question ? `<p>${escapeHtml(question)}</p><p></p>` : null,
    });
    void this.router.navigate([this.brand.base(), 'submit']);
  }
}
