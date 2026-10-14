import { ChangeDetectionStrategy, Component, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, EngineeringApi, EngineeringBoardDto, EngineeringReleaseDto, Guid, saveErrorMessage, validationMessages } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDialogService, SdDrawer, SdEmptyState, SdFormField, SdInput, SdSkeleton, ToastService } from '@sd/ui';

interface ReleaseForm {
  id: Guid | null;
  name: string;
  notes: string;
  productId: Guid | null;
  plannedDate: string;
  rowVersion: string | null;
}

/**
 * Releases of the built-in tracker: plan versions, assign work items (in the work item drawer) and publish — publishing
 * marks every assigned work item as released in that version, so linked tickets get notes and (when enabled) the
 * customers are told "fix released in version X".
 */
@Component({
  selector: 'desk-releases-drawer',
  imports: [FormsModule, SdDrawer, SdButton, SdBadge, SdFormField, SdInput, SdEmptyState, SdSkeleton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer [(open)]="open" title="Releases" width="560px">
      @if (canManage()) {
        <form class="form" (ngSubmit)="save()" [attr.aria-label]="form().id ? 'Edit release' : 'New release'">
          <div class="grid">
            <sd-form-field label="Version" required [error]="errors()['name']">
              <input sdInput name="name" maxlength="100" placeholder="8.1.0" [ngModel]="form().name" (ngModelChange)="patch({ name: $event })" />
            </sd-form-field>
            <sd-form-field label="Planned date" optional>
              <input sdInput name="planned" type="date" [ngModel]="form().plannedDate" (ngModelChange)="patch({ plannedDate: $event })" />
            </sd-form-field>
          </div>
          <sd-form-field label="Product" optional [error]="errors()['productId']">
            <select sdInput name="product" [ngModel]="form().productId" (ngModelChange)="patch({ productId: $event })">
              <option [ngValue]="null">—</option>
              @for (p of board().products; track p.id) {
                <option [ngValue]="p.id">{{ p.name }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="Release notes" optional>
            <textarea sdInput name="notes" rows="2" maxlength="4000" [ngModel]="form().notes" (ngModelChange)="patch({ notes: $event })"></textarea>
          </sd-form-field>
          @if (formError()) {
            <p class="error" role="alert">{{ formError() }}</p>
          }
          <div class="row">
            @if (form().id) {
              <button sdButton type="button" variant="ghost" (click)="reset()">Cancel edit</button>
            }
            <span class="sd-spacer"></span>
            <button sdButton type="submit" variant="primary" [icon]="form().id ? 'check' : 'plus'" [loading]="saving()">{{ form().id ? 'Save release' : 'Add release' }}</button>
          </div>
        </form>
      }

      @if (loading()) {
        <sd-skeleton [lines]="4" height="40px" />
      } @else if (loadError()) {
        <p class="error" role="alert">{{ loadError() }} <button sdButton size="sm" variant="ghost" icon="refresh" (click)="load()">Retry</button></p>
      } @else if (releases().length === 0) {
        <sd-empty-state compact icon="rocket" title="No releases yet" message="Add the next version, assign work items to it, and publish it when it ships." />
      } @else {
        <ul class="list">
          @for (r of releases(); track r.id) {
            <li class="release">
              <div class="main">
                <div class="title">
                  <strong>{{ r.name }}</strong>
                  @if (r.deployedAt) {
                    <sd-badge size="sm" tone="success" icon="rocket">Deployed</sd-badge>
                  } @else if (r.releasedAt) {
                    <sd-badge size="sm" tone="success">Released</sd-badge>
                  } @else {
                    <sd-badge size="sm" tone="info" variant="outline">Planned</sd-badge>
                  }
                  @if (r.productName) {
                    <span class="sd-muted small">{{ r.productName }}</span>
                  }
                </div>
                <span class="sd-muted small">
                  {{ r.itemCount }} item{{ r.itemCount === 1 ? '' : 's' }}{{ r.openItemCount ? ' · ' + r.openItemCount + ' still open' : '' }}
                  @if (r.releasedAt) {
                    · released {{ r.releasedAt | sdDateTime }}
                  } @else if (r.plannedDate) {
                    · planned {{ r.plannedDate }}
                  }
                </span>
                @if (r.notes) {
                  <p class="notes">{{ r.notes }}</p>
                }
              </div>
              @if (canManage()) {
                <div class="actions">
                  @if (!r.releasedAt) {
                    <button sdButton size="sm" variant="primary" icon="rocket" [loading]="busy() === r.id" (click)="publish(r, false)">Publish</button>
                    <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + r.name" (click)="edit(r)"></button>
                    <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + r.name" [disabled]="r.itemCount > 0" (click)="remove(r)"></button>
                  } @else if (!r.deployedAt) {
                    <button sdButton size="sm" icon="server" [loading]="busy() === r.id" (click)="publish(r, true)">Mark deployed</button>
                  }
                </div>
              }
            </li>
          }
        </ul>
      }
    </sd-drawer>
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin-bottom: 16px;
      padding-bottom: 16px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }
    @media (max-width: 480px) {
      .grid {
        grid-template-columns: 1fr;
      }
    }
    .row {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .list {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .release {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: 8px;
      padding: 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    .main {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: 4px;
      min-width: 0;
    }
    .title {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .notes {
      margin: 0;
      font-size: var(--sd-text-sm);
      color: var(--sd-text-2);
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 4px;
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .error {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class ReleasesDrawerComponent {
  readonly open = model(false);
  readonly board = input.required<EngineeringBoardDto>();
  readonly canManage = input(false);
  /** Emitted after a release was created, changed or published (the board reloads). */
  readonly changed = output<void>();

  private readonly api = inject(EngineeringApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);

  protected readonly releases = signal<EngineeringReleaseDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly form = signal<ReleaseForm>(this.empty());
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly busy = signal<Guid | null>(null);

  constructor() {
    effect(() => {
      if (!this.open()) return;
      untracked(() => {
        this.reset();
        this.load();
      });
    });
  }

  private empty(): ReleaseForm {
    return { id: null, name: '', notes: '', productId: null, plannedDate: '', rowVersion: null };
  }

  protected reset(): void {
    this.form.set(this.empty());
    this.errors.set({});
    this.formError.set(null);
  }

  protected load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.releases(this.board().trackerId).subscribe({
      next: (r) => {
        this.releases.set(r);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(err instanceof ApiError ? err.userMessage : 'Releases could not be loaded.');
      },
    });
  }

  protected patch(change: Partial<ReleaseForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
  }

  protected edit(r: EngineeringReleaseDto): void {
    this.form.set({ id: r.id, name: r.name, notes: r.notes ?? '', productId: r.productId, plannedDate: r.plannedDate ?? '', rowVersion: r.rowVersion });
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const f = this.form();
    const request = {
      trackerId: this.board().trackerId,
      name: f.name.trim(),
      notes: f.notes.trim() || null,
      productId: f.productId,
      plannedDate: f.plannedDate || null,
      rowVersion: f.rowVersion,
    };
    this.saving.set(true);
    const call = f.id ? this.api.updateRelease(f.id, request) : this.api.createRelease(request);
    call.subscribe({
      next: (r) => {
        this.saving.set(false);
        this.toast.success(f.id ? 'Release saved' : 'Release added', r.name);
        this.reset();
        this.load();
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const fields = validationMessages(err);
        this.errors.set(fields);
        this.formError.set(Object.keys(fields).length > 0 ? null : saveErrorMessage(err));
      },
    });
  }

  protected async publish(r: EngineeringReleaseDto, deployed: boolean): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: deployed ? `Mark ${r.name} as deployed?` : `Publish ${r.name}?`,
      message: deployed
        ? 'Records the deployment date for the release.'
        : `${r.itemCount} work item(s) become released in ${r.name}. Linked tickets get an internal note, assignees are notified and — when auto-notify is on — customers are told the fix is released.`,
      confirmLabel: deployed ? 'Mark deployed' : 'Publish',
    });
    if (!ok) return;
    this.busy.set(r.id);
    this.api.publishRelease(r.id, { deployed, rowVersion: r.rowVersion }).subscribe({
      next: (updated) => {
        this.busy.set(null);
        this.toast.success(deployed ? 'Deployment recorded' : 'Release published', updated.name);
        this.load();
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(null);
        this.toast.error('Could not publish', err instanceof ApiError ? err.userMessage : undefined);
      },
    });
  }

  protected async remove(r: EngineeringReleaseDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete release ${r.name}?`, confirmLabel: 'Delete', danger: true });
    if (!ok) return;
    this.api.deleteRelease(r.id).subscribe({
      next: () => {
        this.toast.success('Release deleted', r.name);
        this.load();
        this.changed.emit();
      },
      error: (err: unknown) => this.toast.error('Could not delete', err instanceof ApiError ? err.userMessage : undefined),
    });
  }
}
