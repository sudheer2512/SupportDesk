import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { SdBreadcrumb } from '@sd/ui';
import { ModulePlaceholderComponent } from '../../shared/module-placeholder.component';

/** Tickets landing (list, new, detail) — an honest placeholder until the module's screens are built. */
@Component({
  selector: 'desk-tickets-home',
  imports: [ModulePlaceholderComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <desk-module-placeholder
      [title]="title()"
      icon="ticket"
      [subtitle]="subtitle()"
      [planned]="planned"
      [breadcrumbs]="breadcrumbs()"
    />
  `,
})
export class TicketsHomePage {
  /** Route data / params (component input binding). */
  readonly mode = input<'list' | 'new' | 'detail'>('list');
  readonly id = input<string | undefined>();

  protected readonly title = computed(() => (this.mode() === 'new' ? 'New ticket' : this.mode() === 'detail' ? 'Ticket' : 'Tickets'));
  protected readonly subtitle = computed(() =>
    this.mode() === 'detail' && this.id()
      ? `Ticket ${this.id()} will open in the three-panel workbench.`
      : this.mode() === 'new'
        ? 'The ticket form (layouts, custom fields, attachments) is coming next.'
        : 'Saved views, the ticket list and the three-panel agent workbench.',
  );
  protected readonly breadcrumbs = computed<SdBreadcrumb[]>(() =>
    this.mode() === 'list' ? [] : [{ label: 'Tickets', link: '/tickets' }, { label: this.title() }],
  );
  protected readonly planned = [
    'Saved views with live counts and the condition builder',
    'Virtualised ticket list with sorting, column chooser and bulk actions',
    'Three-panel workbench: list, conversation timeline, customer context',
    'Replies, internal notes, drafts and reply-collision detection',
    'Assign, transfer, escalate, resolve, merge, split and link',
    'Tasks, time entries, follow-ups and watchers',
  ];
}
