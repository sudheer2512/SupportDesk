import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminNotificationsApi, ApiError, NotificationTemplateDto } from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { ADMIN_EDITOR_STYLES } from '../admin-shared';

/** E-mail templates sent automatically to customers and agents; built-in defaults until customised. */
@Component({
  selector: 'desk-admin-templates',
  imports: [RouterLink, SdButton, SdBadge, SdIcon, SdEmptyState, SdSkeleton, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="4" height="56px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Templates could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else {
      @for (group of groups(); track group.audience) {
        <section class="group" [attr.aria-labelledby]="'tg-' + group.audience">
          <h2 [id]="'tg-' + group.audience"><sd-icon [name]="group.audience === 'Customer' ? 'users' : 'headphones'" [size]="15" /> {{ group.audience === 'Customer' ? 'To customers' : 'To agents' }}</h2>
          <ul class="rows">
            @for (t of group.items; track t.key) {
              <li>
                <span class="icon"><sd-icon name="mail" [size]="16" /></span>
                <div class="grow">
                  <div class="title">
                    <a [routerLink]="t.key" class="name">{{ t.name }}</a>
                    @if (t.isCustomized && t.isActive) {
                      <sd-badge size="sm" tone="primary">Customised</sd-badge>
                    } @else if (t.isCustomized) {
                      <sd-badge size="sm" tone="warning">Custom (inactive)</sd-badge>
                    } @else {
                      <sd-badge size="sm">Default</sd-badge>
                    }
                  </div>
                  <span class="sd-muted small">{{ t.description }}</span>
                  <span class="sd-muted small subject">Subject: {{ t.subject }}@if (t.updatedAt) { · updated {{ t.updatedAt | sdRelativeTime }}}</span>
                </div>
                <a sdButton size="sm" icon="pencil" [routerLink]="t.key" [attr.aria-label]="'Edit ' + t.name">Edit</a>
              </li>
            }
          </ul>
        </section>
      } @empty {
        <sd-empty-state icon="mail" title="No templates" message="No e-mail templates are available." />
      }
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .group { margin-bottom: var(--sd-space-5); }
    h2 { display: flex; align-items: center; gap: 6px; margin: 0 0 var(--sd-space-2); font-size: var(--sd-text-sm); color: var(--sd-text-2); }
    .icon { display: grid; place-items: center; width: 32px; height: 32px; flex: none; border-radius: var(--sd-radius-sm); background: var(--sd-primary-soft); color: var(--sd-primary); }
    .grow { display: flex; flex-direction: column; gap: 2px; }
    .title { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .name { font-weight: var(--sd-weight-semibold); color: var(--sd-text); text-decoration: none; }
    .name:hover, .name:focus-visible { color: var(--sd-primary); text-decoration: underline; }
    .subject { font-family: var(--sd-font-mono); }
  `,
})
export class TemplatesPage implements OnInit {
  private readonly api = inject(AdminNotificationsApi);

  protected readonly templates = signal<NotificationTemplateDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly groups = computed(() =>
    (['Customer', 'Agent'] as const).map((audience) => ({ audience, items: this.templates().filter((t) => t.audience === audience) })).filter((g) => g.items.length > 0),
  );

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.templates().subscribe({
      next: (list) => {
        this.templates.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }
}
