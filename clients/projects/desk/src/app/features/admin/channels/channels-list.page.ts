import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ChannelAccountDto, ChannelProviderDto, ChannelsApi } from '@sd/core';
import { SdBadge, SdButton, SdCard, SdEmptyState, SdIcon, SdPageHeader, SdRelativeTimePipe, SdSkeleton, SdTooltip, ToastService } from '@sd/ui';
import { forkJoin } from 'rxjs';
import { accountStatusTone } from '../../../shared/channel-ui';
import { TYPE_META } from './provider-fields';

/** /admin/channels — every channel account grouped by type with status, last poll/error, test and poll actions. */
@Component({
  selector: 'desk-channels-list-page',
  imports: [RouterLink, SdPageHeader, SdCard, SdButton, SdBadge, SdIcon, SdEmptyState, SdSkeleton, SdTooltip, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Channels" subtitle="Mailboxes, WhatsApp, SMS, live chat, voice lines and social pages." icon="radio-tower" [breadcrumbs]="crumbs">
        <a sdButton sdPageActions variant="secondary" icon="play" routerLink="simulator">Simulator</a>
        <a sdButton sdPageActions variant="primary" icon="plus" routerLink="new">New channel</a>
      </sd-page-header>
      <div class="desk-page__body">
        @if (error(); as e) {
          <sd-empty-state icon="circle-alert" title="Channels could not be loaded" [message]="e">
            <button sdButton (click)="load()">Retry</button>
          </sd-empty-state>
        } @else if (!loaded()) {
          <sd-card><sd-skeleton [lines]="5" /></sd-card>
        } @else if (channels().length === 0) {
          <sd-empty-state icon="radio-tower" title="No channels yet" message="Connect a mailbox or add the chat widget to start receiving conversations.">
            <a sdButton variant="primary" icon="plus" routerLink="new">New channel</a>
          </sd-empty-state>
        } @else {
          @for (group of groups(); track group.type) {
            <section class="group" [attr.aria-label]="group.label">
              <h2><sd-icon [name]="group.icon" [size]="16" /> {{ group.label }} <span class="sd-muted sd-small">({{ group.items.length }})</span></h2>
              <div class="grid">
                @for (c of group.items; track c.id) {
                  <sd-card padding="md" class="channel" [class.inactive]="!c.isActive">
                    <div class="top">
                      <a class="name sd-truncate" [routerLink]="[c.id]">{{ c.name }}</a>
                      <sd-badge size="sm" dot [tone]="statusTone(c.status)">{{ c.status }}</sd-badge>
                    </div>
                    <div class="addr sd-small sd-mono sd-truncate" [title]="c.address">{{ c.address }}</div>
                    <div class="meta sd-xs sd-muted">
                      <span>{{ providerLabel(c.provider) }}</span>
                      @if (c.lastPolledAt) {
                        <span>· polled {{ c.lastPolledAt | sdRelativeTime }}</span>
                      }
                      @if (c.webhookPath) {
                        <span>· webhook</span>
                      }
                    </div>
                    @if (c.lastError) {
                      <p class="err sd-small" role="note"><sd-icon name="circle-alert" [size]="12" /> {{ c.lastError }}</p>
                    }
                    <div class="actions">
                      <a sdButton size="sm" variant="secondary" icon="pencil" [routerLink]="[c.id]">Configure</a>
                      <button sdButton size="sm" variant="ghost" icon="zap" [loading]="busy() === c.id + ':test'" (click)="test(c)">Test</button>
                      @if (polls(c)) {
                        <button sdButton size="sm" variant="ghost" icon="refresh" [loading]="busy() === c.id + ':poll'" (click)="poll(c)" sdTooltip="Fetch new mail now">Poll</button>
                      }
                      <a sdButton size="sm" variant="ghost" icon="play" routerLink="simulator" [queryParams]="{ channel: c.id }" sdTooltip="Simulate inbound traffic">Simulate</a>
                    </div>
                  </sd-card>
                }
              </div>
            </section>
          }
        }
      </div>
    </div>
  `,
  styles: `
    .group + .group {
      margin-top: var(--sd-space-6);
    }
    h2 {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      margin: 0 0 var(--sd-space-3);
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
      gap: var(--sd-space-3);
    }
    .channel.inactive {
      opacity: 0.7;
    }
    .top {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--sd-space-2);
    }
    .name {
      font-weight: var(--sd-weight-semibold);
      color: var(--sd-text);
      text-decoration: none;
    }
    .name:hover {
      text-decoration: underline;
    }
    .addr {
      margin-top: 4px;
      color: var(--sd-text-2);
    }
    .meta {
      display: flex;
      gap: 4px;
      flex-wrap: wrap;
      margin-top: 4px;
    }
    .err {
      margin: var(--sd-space-2) 0 0;
      color: var(--sd-danger);
      overflow-wrap: anywhere;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: var(--sd-space-1);
      margin-top: var(--sd-space-3);
    }
  `,
})
export class ChannelsListPage {
  private readonly api = inject(ChannelsApi);
  private readonly toast = inject(ToastService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Channels' }];
  protected readonly channels = signal<ChannelAccountDto[]>([]);
  protected readonly providers = signal<ChannelProviderDto[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal<string | null>(null);
  protected readonly statusTone = accountStatusTone;

  protected readonly groups = computed(() =>
    Object.keys(TYPE_META)
      .map((type) => ({ type, label: TYPE_META[type].label, icon: TYPE_META[type].icon, items: this.channels().filter((c) => c.type === type) }))
      .filter((g) => g.items.length > 0),
  );

  constructor() {
    this.load();
  }

  load(): void {
    this.error.set(null);
    forkJoin({ channels: this.api.list(), providers: this.api.providers() }).subscribe({
      next: ({ channels, providers }) => {
        this.channels.set(channels);
        this.providers.set(providers);
        this.loaded.set(true);
      },
      error: (e: unknown) => this.error.set(ApiError.from(e).userMessage),
    });
  }

  protected providerLabel(provider: string): string {
    return provider === 'Simulator' ? 'Simulator mode' : provider;
  }

  protected polls(c: ChannelAccountDto): boolean {
    return c.type === 'Email' && c.isActive && (this.providers().find((p) => p.provider === c.provider)?.polling ?? false);
  }

  protected test(c: ChannelAccountDto): void {
    this.busy.set(`${c.id}:test`);
    this.api.test(c.id).subscribe({
      next: (r) => {
        this.busy.set(null);
        if (r.success) this.toast.success(`${c.name}: connection OK`, r.message);
        else this.toast.error(`${c.name}: connection failed`, r.message);
        this.load();
      },
      error: (e: unknown) => {
        this.busy.set(null);
        this.toast.error('Test failed', ApiError.from(e).userMessage);
      },
    });
  }

  protected poll(c: ChannelAccountDto): void {
    this.busy.set(`${c.id}:poll`);
    this.api.poll(c.id).subscribe({
      next: (r) => {
        this.busy.set(null);
        if (r.error) this.toast.error(`${c.name}: polling failed`, r.error);
        else this.toast.success(`${c.name}: ${r.fetched} fetched`, `${r.processed} processed, ${r.duplicates} duplicate(s), ${r.failed} failed.`);
        this.load();
      },
      error: () => this.busy.set(null),
    });
  }
}
