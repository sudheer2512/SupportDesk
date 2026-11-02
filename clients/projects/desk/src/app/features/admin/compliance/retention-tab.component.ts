import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ApiError, ComplianceApi, RetentionDataTypeDto, RetentionDryRunDto, RetentionPolicyDto, SaveRetentionPolicyRequest } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDialogService, SdDrawer, SdEmptyState, SdFormField, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { Subject, catchError, debounceTime, of, switchMap } from 'rxjs';
import { SAAS_CSS } from '../../subscription/saas-ui';

/** Retention policies: per data type, an action after N days, a live dry-run count, run now; legal holds always win. */
@Component({
  selector: 'desk-retention-tab',
  imports: [FormsModule, SdBadge, SdButton, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdInput, SdSkeleton, SdToggle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toolbar">
      <p class="muted small grow">Active policies run every six hours. Records covered by an active legal hold are never archived, anonymised or deleted.</p>
      <button sdButton variant="primary" icon="plus" (click)="edit(null)">New policy</button>
    </div>
    @if (error(); as e) {
      <sd-empty-state icon="circle-alert" title="Policies could not be loaded" [message]="e"><button sdButton (click)="load()">Try again</button></sd-empty-state>
    } @else if (policies() === null) {
      <sd-skeleton [lines]="4" height="40px" />
    } @else if (policies()!.length === 0) {
      <sd-empty-state icon="archive" title="No retention policies" message="Keep data only as long as you need it: archive closed tickets, purge old messages, attachments, AI logs and audit records.">
        <button sdButton variant="primary" (click)="edit(null)">Create a policy</button>
      </sd-empty-state>
    } @else {
      <div class="table-wrap">
        <table class="data">
          <caption class="sd-visually-hidden">Retention policies</caption>
          <thead>
            <tr><th scope="col">Policy</th><th scope="col">Rule</th><th scope="col" class="hide-sm">Last run</th><th scope="col">Active</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr>
          </thead>
          <tbody>
            @for (p of policies(); track p.id) {
              <tr>
                <td><strong>{{ p.name }}</strong></td>
                <td>{{ p.action }} · {{ typeLabel(p.dataType) }} older than {{ days(p.afterDays) }}</td>
                <td class="hide-sm">{{ p.lastRunAt ? (p.lastRunAt | sdDateTime) + ' · ' + p.lastRunAffected + ' affected' : 'never' }}</td>
                <td><sd-badge size="sm" [tone]="p.isActive ? 'success' : 'neutral'">{{ p.isActive ? 'Active' : 'Paused' }}</sd-badge></td>
                <td class="actions">
                  <button sdButton size="sm" variant="ghost" (click)="dryRun(p)" [loading]="busyId() === p.id + ':dry'">Dry run</button>
                  <button sdButton size="sm" (click)="run(p)" [loading]="busyId() === p.id + ':run'">Run now</button>
                  <button sdButton size="sm" variant="ghost" icon="pencil" iconOnly [attr.aria-label]="'Edit ' + p.name" (click)="edit(p)"></button>
                  <button sdButton size="sm" variant="ghost" icon="trash" iconOnly [attr.aria-label]="'Delete ' + p.name" (click)="remove(p)"></button>
                </td>
              </tr>
              @if (results()[p.id]; as r) {
                <tr class="result">
                  <td colspan="5">
                    <span class="small">
                      Dry run at {{ r.evaluatedAt | sdDateTime }}: <strong>{{ r.eligible }}</strong> record(s) would be {{ verb(r.action) }}; <strong>{{ r.heldBack }}</strong> kept by legal holds (cut-off {{ r.cutoff | sdDateTime: 'date' }}).
                    </span>
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      </div>
    }

    <sd-drawer [(open)]="editorOpen" [title]="editingId() ? 'Edit retention policy' : 'New retention policy'" width="520px">
      <form class="form" id="retention-form" (ngSubmit)="save()">
        <sd-form-field label="Name" required [error]="errors()['name']"><input sdInput name="name" [(ngModel)]="form.name" maxlength="200" /></sd-form-field>
        <sd-form-field label="Data" required [error]="errors()['dataType']" [hint]="currentType()?.description ?? null">
          <select sdInput name="type" [ngModel]="form.dataType" (ngModelChange)="setType($event)">
            @for (t of types(); track t.key) {
              <option [value]="t.key">{{ t.label }}</option>
            }
          </select>
        </sd-form-field>
        <div class="form-grid">
          <sd-form-field label="Action" required [error]="errors()['action']">
            <select sdInput name="action" [ngModel]="form.action" (ngModelChange)="form.action = $event; changed()">
              @for (a of currentType()?.actions ?? []; track a) {
                <option [value]="a">{{ a }}</option>
              }
            </select>
          </sd-form-field>
          <sd-form-field label="After (days)" required [hint]="'At least ' + (currentType()?.minimumDays ?? 0) + ' days'" [error]="errors()['afterDays']">
            <input sdInput type="number" name="days" [min]="currentType()?.minimumDays ?? 1" [ngModel]="form.afterDays" (ngModelChange)="form.afterDays = +$event; changed()" />
          </sd-form-field>
        </div>
        <sd-toggle name="active" [value]="form.isActive" (valueChange)="form.isActive = $event">Run automatically every day</sd-toggle>
        <div class="callout callout--info" role="status" aria-live="polite">
          @if (preview(); as pv) {
            <div class="grow">Right now <strong>{{ pv.eligible }}</strong> record(s) would be {{ verb(pv.action) }}; <strong>{{ pv.heldBack }}</strong> are kept by legal holds.</div>
          } @else if (previewError()) {
            <div class="grow">{{ previewError() }}</div>
          } @else {
            <div class="grow muted">Calculating what this policy would affect…</div>
          }
        </div>
        @if (formError()) {
          <p class="err" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="editorOpen.set(false)">Cancel</button>
        <button sdButton type="submit" form="retention-form" variant="primary" [loading]="saving()">{{ editingId() ? 'Save' : 'Create policy' }}</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .grow { flex: 1 1 240px; margin: 0; }
    tr.result td { background: var(--sd-surface-2); }
  `,
})
export class RetentionTabComponent implements OnInit {
  private readonly api = inject(ComplianceApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(SdDialogService);
  private readonly preview$ = new Subject<SaveRetentionPolicyRequest>();

  protected readonly types = signal<RetentionDataTypeDto[]>([]);
  protected readonly policies = signal<RetentionPolicyDto[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<Record<string, RetentionDryRunDto>>({});
  protected readonly busyId = signal<string | null>(null);
  protected readonly editorOpen = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly preview = signal<RetentionDryRunDto | null>(null);
  protected readonly previewError = signal<string | null>(null);
  protected readonly typeKey = signal('ClosedTickets');
  protected form: SaveRetentionPolicyRequest = { name: '', dataType: 'ClosedTickets', action: 'Archive', afterDays: 730, isActive: true };
  protected readonly currentType = computed(() => this.types().find((t) => t.key === this.typeKey()) ?? null);

  constructor() {
    this.preview$
      .pipe(
        debounceTime(400),
        switchMap((r) => {
          this.preview.set(null);
          this.previewError.set(null);
          return this.api.dryRunProposed(r).pipe(
            catchError((e: ApiError) => {
              this.previewError.set(e.fieldError('afterDays') ?? e.fieldError('action') ?? e.userMessage);
              return of(null);
            }),
          );
        }),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((p) => this.preview.set(p));
  }

  ngOnInit(): void {
    this.api.dataTypes().subscribe({ next: (t) => this.types.set(t), error: () => undefined });
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    this.api.policies().subscribe({ next: (p) => this.policies.set(p), error: (e: ApiError) => this.error.set(e.userMessage) });
  }

  protected typeLabel(key: string): string {
    return this.types().find((t) => t.key === key)?.label ?? key;
  }

  protected days(n: number): string {
    return n % 365 === 0 && n >= 365 ? `${n / 365} year${n === 365 ? '' : 's'}` : `${n} days`;
  }

  protected verb(action: string): string {
    return action === 'Archive' ? 'archived' : action === 'Anonymize' ? 'anonymised' : 'deleted';
  }

  protected edit(p: RetentionPolicyDto | null): void {
    this.errors.set({});
    this.formError.set(null);
    this.editingId.set(p?.id ?? null);
    this.form = p
      ? { name: p.name, dataType: p.dataType, action: p.action, afterDays: p.afterDays, isActive: p.isActive }
      : { name: '', dataType: 'ClosedTickets', action: 'Archive', afterDays: 730, isActive: true };
    this.typeKey.set(this.form.dataType);
    this.editorOpen.set(true);
    this.changed();
  }

  protected setType(key: string): void {
    const t = this.types().find((x) => x.key === key);
    this.typeKey.set(key);
    this.form.dataType = key;
    if (t && !t.actions.includes(this.form.action)) this.form.action = t.actions[0];
    if (t && this.form.afterDays < t.minimumDays) this.form.afterDays = t.minimumDays;
    this.changed();
  }

  protected changed(): void {
    this.preview$.next({ ...this.form, name: this.form.name || 'preview' });
  }

  protected save(): void {
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const id = this.editingId();
    (id ? this.api.updatePolicy(id, this.form) : this.api.createPolicy(this.form)).subscribe({
      next: () => {
        this.saving.set(false);
        this.editorOpen.set(false);
        this.toast.success(id ? 'Policy saved' : 'Policy created');
        this.load();
      },
      error: (e: ApiError) => {
        this.saving.set(false);
        const map = { name: e.fieldError('name'), dataType: e.fieldError('dataType'), action: e.fieldError('action'), afterDays: e.fieldError('afterDays') };
        this.errors.set(map);
        if (!Object.values(map).some(Boolean)) this.formError.set(e.userMessage);
      },
    });
  }

  protected dryRun(p: RetentionPolicyDto): void {
    this.busyId.set(p.id + ':dry');
    this.api.dryRun(p.id).subscribe({
      next: (r) => {
        this.results.set({ ...this.results(), [p.id]: r });
        this.busyId.set(null);
      },
      error: () => this.busyId.set(null),
    });
  }

  protected async run(p: RetentionPolicyDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: `Run “${p.name}” now?`,
      message: `Eligible records are ${this.verb(p.action)} immediately (up to 1,000 per run). ${p.action === 'Delete' ? 'Deleted records cannot be recovered.' : ''}`,
      confirmLabel: 'Run now',
      danger: p.action !== 'Archive',
    });
    if (!ok) return;
    this.busyId.set(p.id + ':run');
    this.api.run(p.id).subscribe({
      next: (r) => {
        this.busyId.set(null);
        this.toast.success('Retention run complete', `${r.affected} affected, ${r.heldBack} kept by legal holds.`);
        this.load();
      },
      error: (e: ApiError) => {
        this.busyId.set(null);
        this.toast.error('Run failed', e.userMessage);
      },
    });
  }

  protected async remove(p: RetentionPolicyDto): Promise<void> {
    if (!(await this.dialog.confirm({ title: `Delete “${p.name}”?`, danger: true, confirmLabel: 'Delete' }))) return;
    this.api.deletePolicy(p.id).subscribe({ next: () => this.load() });
  }
}
