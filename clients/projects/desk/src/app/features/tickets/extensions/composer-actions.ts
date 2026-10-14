import { inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AiApi, Permission, TicketDetailDto } from '@sd/core';
import { SdDialogService } from '@sd/ui';

export type ComposerMode = 'reply' | 'note' | 'handover';

/** What a composer action can see and do. */
export interface ComposerActionContext {
  ticket: TicketDetailDto;
  mode: ComposerMode;
  /** Current editor content (HTML, '' when empty). */
  html(): string;
  /** Inserts HTML at the cursor (e.g. an AI draft paragraph, an article link). */
  insertHtml(html: string): void;
  /** Replaces the whole editor content. */
  replaceHtml(html: string): void;
  /** Shows a transient message under the composer (errors, "draft inserted"). */
  notify(message: string, kind?: 'info' | 'error'): void;
}

/** A button in the composer toolbar contributed by another module (AI drafts, KB article links, macros…). */
export interface ComposerActionDef {
  /** Unique, stable id, e.g. "ai.draft-reply". */
  key: string;
  label: string;
  /** @sd/ui icon name. */
  icon: string;
  /** Shown only when the user holds this permission (any of, for a list). */
  permission?: Permission | string | readonly (Permission | string)[];
  /** Optional plan feature flag (MeStore.hasFeature). */
  feature?: string;
  /** Composer tabs it applies to (default: all). */
  modes?: readonly ComposerMode[];
  /**
   * Runs inside the composer's injection context, so it may call `inject()` (e.g. `inject(AiApi)`) before its
   * first `await`. Errors thrown or rejected are shown to the agent.
   */
  run: (ctx: ComposerActionContext) => void | Promise<void>;
}

/**
 * Contributed composer actions. Modules append their entries here, e.g.
 * ```ts
 * { key: 'ai.draft', label: 'Draft with AI', icon: 'sparkles', permission: 'ai.use', modes: ['reply'],
 *   run: async (ctx) => { const ai = inject(AiApi); ctx.insertHtml((await firstValueFrom(ai.draftReply(ctx.ticket.id))).html); } },
 * ```
 */
export const COMPOSER_ACTIONS: ComposerActionDef[] = [
  {
    key: 'ai.draft-reply',
    label: 'Draft with AI',
    icon: 'sparkles',
    permission: 'ai.use',
    feature: 'ai',
    modes: ['reply'],
    run: async (ctx) => {
      const ai = inject(AiApi);
      const draft = await firstValueFrom(ai.suggestReply(ctx.ticket.id, {}, { silent: true }));
      if (ctx.html().replace(/<[^>]*>/g, '').trim()) ctx.insertHtml(draft.bodyHtml);
      else ctx.replaceHtml(draft.bodyHtml);
      ctx.notify(
        draft.grounded
          ? `AI draft inserted (${draft.citations.length} source${draft.citations.length === 1 ? '' : 's'}) — review before sending.`
          : 'AI draft inserted, but no matching article or resolved ticket was found — review it carefully.',
        draft.grounded ? 'info' : 'error',
      );
    },
  },
  {
    key: 'collaboration.share',
    label: 'Share to Teams/Slack',
    icon: 'share',
    permission: 'tickets.note',
    modes: ['note'],
    run: async (ctx) => {
      const dialogs = inject(SdDialogService);
      const { ShareToChannelDialog } = await import('../../engineering/share/share-to-channel.dialog');
      const comment = ctx.html().replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
      const ref = dialogs.open<string | null>(ShareToChannelDialog, {
        data: { ticketId: ctx.ticket.id, ticketLabel: `${ctx.ticket.displayNumber} “${ctx.ticket.subject}”`, comment },
        size: 'sm',
      });
      const channel = await firstValueFrom(ref.closed);
      if (channel) ctx.notify(`Shared to ${channel}.`);
    },
  },
];
