import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminLayoutDto, ApiError, MetaApi, TicketConfigApi } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES } from '../admin-shared';

/** Form layouts: agent and portal ticket forms per department, brand and channel. Opens the drag-and-drop builder. */
@Component({
  selector: 'desk-admin-layouts',
  imports: [RouterLink, SdButton, SdBadge, SdIcon, SdEmptyState, SdSkeleton, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <p class="sd-muted intro">The most specific active layout wins: department (and brand or channel) first, then the default layout of the form.</p>
      <span class="sd-spacer"></span>
      <a sdButton variant="primary" icon="plus" routerLink="new">New layout</a>
    </div>
    @if (loading()) {
      <sd-skeleton [lines]="5" height="56px" />
    } @else if (error()) {
      <sd-empty-state icon="circle-alert" title="Layouts could not be loaded" [message]="error()">
        <button sdButton icon="refresh" (click)="load()">Try again</button>
      </sd-empty-state>
    } @else if (layouts().length === 0) {
      <sd-empty-state icon="layers" title="No form layouts yet" message="Without a layout, forms show every applicable field in their default order.">
        <a sdButton variant="primary" icon="plus" routerLink="new">Design a form</a>
      </sd-empty-state>
    } @else {
      @for (group of groups(); track group.kind) {
        <section class="group" [attr.aria-labelledby]="'lk-' + group.kind">
          <h2 [id]="'lk-' + group.kind"><sd-icon [name]="group.kind === 'AgentForm' ? 'headphones' : 'globe'" [size]="15" /> {{ group.title }}</h2>
          @if (group.items.length === 0) {
            <p class="sd-muted small">No {{ group.title.toLowerCase() }} yet.</p>
          } @else {
            <ul class="rows">
              @for (l of group.items; track l.id) {
                <li [class.inactive]="!l.isActive">
                  <div class="grow">
                    <div class="title">
                      <a [routerLink]="l.id" class="name">{{ l.name }}</a>
                      @if (l.isDefault) {
                        <sd-badge size="sm" tone="primary">Default</sd-badge>
                      }
                      @if (!l.isActive) {
                        <sd-badge size="sm">Inactive</sd-badge>
                      }
                    </div>
                    <span class="sd-muted small">{{ scope(l) }} · {{ fieldCount(l) }} fields in {{ l.sections.length }} section(s) · updated {{ l.updatedAt | sdRelativeTime }}</span>
                  </div>
                  <a sdButton size="sm" icon="pencil" [routerLink]="l.id" [attr.aria-label]="'Edit ' + l.name">Edit</a>
                  <button sdButton size="sm" variant="ghost" iconOnly icon="trash" [attr.aria-label]="'Delete ' + l.name" (click)="remove(l)"></button>
                </li>
              }
            </ul>
          }
        </section>
      }
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .intro { margin: 0; max-width: 720px; font-size: var(--sd-text-sm); }
    .group { margin-bottom: var(--sd-space-5); }
    h2 { display: flex; align-items: center; gap: 6px; margin: 0 0 var(--sd-space-2); font-size: var(--sd-text-sm); font-weight: var(--sd-weight-semibold); color: var(--sd-text-2); }
    .title { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .name { font-weight: var(--sd-weight-semibold); color: var(--sd-text); text-decoration: none; }
    .name:hover, .name:focus-visible { color: var(--sd-primary); text-decoration: underline; }
    li.inactive .name { color: var(--sd-text-3); }
  `,
})
export class LayoutsPage implements OnInit {
  private readonly api = inject(TicketConfigApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly layouts = signal<AdminLayoutDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly groups = computed(() => [
    { kind: 'AgentForm', title: 'Agent forms', items: this.layouts().filter((l) => l.kind === 'AgentForm') },
    { kind: 'PortalForm', title: 'Portal forms', items: this.layouts().filter((l) => l.kind === 'PortalForm') },
  ]);

  ngOnInit(): void {
    this.meta.load();
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.layouts().subscribe({
      next: (list) => {
        this.layouts.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.error.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  protected scope(l: AdminLayoutDto): string {
    const meta = this.meta.ticketForm();
    const parts = [
      l.departmentId ? (meta?.departments.find((d) => d.id === l.departmentId)?.name ?? 'Department') : 'All departments',
      l.brandId ? (meta?.brands.find((b) => b.id === l.brandId)?.name ?? 'Brand') : null,
      l.channel ? `${l.channel} channel` : null,
    ];
    return parts.filter(Boolean).join(' · ');
  }

  protected fieldCount(l: AdminLayoutDto): number {
    return l.sections.reduce((n, s) => n + (s.fields?.length ?? 0), 0);
  }

  protected async remove(l: AdminLayoutDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete “${l.name}”?`, message: 'Forms fall back to the next matching layout.', danger: true, confirmLabel: 'Delete' });
    if (!ok) return;
    this.api.deleteLayout(l.id).subscribe({
      next: () => {
        this.toast.success('Layout deleted', l.name);
        this.load();
        this.meta.refresh();
      },
      error: (err: unknown) => this.toast.error('Could not delete the layout', ApiError.from(err).userMessage),
    });
  }
}
