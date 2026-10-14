import { Injectable, inject, signal } from '@angular/core';
import { PortalAssistantReplyDto, PortalAuthService, PortalContentApi } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { BrandService } from '../brand.service';
import { browserLanguage, errorMessage } from '../shared/portal-utils';

export interface AssistantTurn {
  id: number;
  role: 'user' | 'assistant' | 'error';
  text: string;
  reply?: PortalAssistantReplyDto;
}

/**
 * In-memory conversation with the brand's help assistant (POST /portal/{brand}/assistant). Survives
 * closing the panel and navigating; cleared on reload or brand change. Nothing is persisted.
 */
@Injectable({ providedIn: 'root' })
export class AssistantService {
  private readonly api = inject(PortalContentApi);
  private readonly brand = inject(BrandService);
  private readonly auth = inject(PortalAuthService);

  private nextId = 1;
  private brandKey: string | null = null;
  readonly turns = signal<AssistantTurn[]>([]);
  readonly busy = signal(false);

  async ask(message: string): Promise<void> {
    const slug = this.brand.slug();
    const text = message.trim();
    if (!slug || !text || this.busy()) return;
    if (this.brandKey !== slug) {
      this.brandKey = slug;
      this.turns.set([]);
    }
    this.turns.update((t) => [...t, { id: this.nextId++, role: 'user', text }]);
    this.busy.set(true);
    try {
      const locale = this.auth.contact()?.preferredLanguage || this.brand.locales()[0] || browserLanguage();
      const reply = await firstValueFrom(this.api.assistant(slug, { message: text, locale }));
      this.turns.update((t) => [...t, { id: this.nextId++, role: 'assistant', text: reply.text, reply }]);
    } catch (err) {
      this.turns.update((t) => [
        ...t,
        { id: this.nextId++, role: 'error', text: errorMessage(err, "Sorry, I can't answer right now. Please try again or submit a ticket.") },
      ]);
    } finally {
      this.busy.set(false);
    }
  }

  /** The most recent question (used to prefill a ticket). */
  lastQuestion(): string | null {
    return [...this.turns()].reverse().find((t) => t.role === 'user')?.text ?? null;
  }

  clear(): void {
    this.turns.set([]);
  }
}
