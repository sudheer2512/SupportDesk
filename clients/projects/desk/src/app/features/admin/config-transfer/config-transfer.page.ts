import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  ConfigDiffItemDto,
  ConfigDraftDto,
  ConfigResolutionAction,
  ConfigResolutionDto,
  ConfigSectionInfoDto,
  ConfigSnapshotDto,
  ConfigTransferApi,
  JsonValue,
  saveBlob,
} from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCard,
  SdCheckbox,
  SdDateTimePipe,
  SdDialogService,
  SdDrawer,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdSkeleton,
  SdTone,
  ToastService,
} from '@sd/ui';
import { SAAS_CSS } from '../../subscription/saas-ui';

/**
 * Configuration transfer (admin.config_transfer): export configuration as versioned JSON (ids remapped to natural keys,
 * no secrets); import a file into a draft and review the diff, choosing skip / overwrite / rename per item; publish
 * (creates a snapshot); capture snapshots; roll back to any snapshot (recorded as a new snapshot).
 */
@Component({
  selector: 'desk-config-transfer-page',
  imports: [FormsModule, SdBadge, SdButton, SdCard, SdCheckbox, SdDateTimePipe, SdDrawer, SdEmptyState, SdFormField, SdIcon, SdInput, SdPageHeader, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      <sd-page-header title="Configuration transfer" icon="share" subtitle="Move configuration between workspaces, review changes and roll back." [breadcrumbs]="crumbs" />
      <div class="desk-page__body">
        <div class="grid">
          <sd-card title="Export" subtitle="Statuses, categories, fields, layouts, SLA, rules, blueprints, templates, KB categories, AI prompts and channels (without secrets).">
            @if (sections() === null) {
              <sd-skeleton [lines]="6" />
            } @else {
              <div class="checks" role="group" aria-label="Sections to export">
                @for (s of sections(); track s.key) {
                  <sd-checkbox [value]="chosen().includes(s.key)" (valueChange)="toggle(s.key, $event)">{{ s.label }} <span class="muted small">({{ s.count }})</span></sd-checkbox>
                }
              </div>
              <div class="footer">
                <button sdButton variant="ghost" size="sm" (click)="chosen.set([])">All sections</button>
                <button sdButton variant="primary" icon="download" [loading]="exporting()" (click)="export()">Export {{ chosen().length ? chosen().length + ' section(s)' : 'everything' }}</button>
              </div>
            }
          </sd-card>
          <sd-card title="Import" subtitle="Upload an export from this or another workspace. Nothing changes until you publish the draft.">
            <label class="drop" [class.busy]="importing()">
              <sd-icon name="upload" [size]="22" />
              <span>{{ importing() ? 'Reading…' : 'Choose a configuration file (.json)' }}</span>
              <input type="file" accept="application/json,.json" (change)="onFile($event)" [disabled]="importing()" aria-label="Configuration file" />
            </label>
            @if (importError()) {
              <p class="err" role="alert">{{ importError() }}</p>
            }
            @if (drafts().length) {
              <h3 class="section-title">Drafts</h3>
              <ul class="list">
                @for (d of drafts(); track d.id) {
                  <li>
                    <div class="grow">
                      <strong>{{ d.label }}</strong>
                      <div class="muted small">{{ d.createdAt | sdDateTime }}{{ d.createdByName ? ' · ' + d.createdByName : '' }}{{ d.sourceTenant ? ' · from ' + d.sourceTenant : '' }}</div>
                    </div>
                    <sd-badge size="sm" [tone]="d.status === 'Draft' ? 'warning' : d.status === 'Published' ? 'success' : 'neutral'">{{ d.status }}</sd-badge>
                    @if (d.status === 'Draft') {
                      <button sdButton size="sm" (click)="openDraft(d.id)">Review</button>
                    }
                  </li>
                }
              </ul>
            }
          </sd-card>
        </div>

        <h2 class="section-title"><sd-icon name="history" [size]="16" />Snapshots</h2>
        <div class="toolbar">
          <p class="muted small grow">A snapshot is the complete configuration at a point in time. Publishing an import and rolling back create snapshots automatically.</p>
          <button sdButton icon="plus" [loading]="capturing()" (click)="capture()">Capture snapshot</button>
        </div>
        @if (snapshots() === null) {
          <sd-skeleton [lines]="4" height="36px" />
        } @else if (snapshots()!.length === 0) {
          <sd-empty-state icon="history" title="No snapshots yet" message="Capture one before making large changes so you can roll back." />
        } @else {
          <div class="table-wrap">
            <table class="data">
              <caption class="sd-visually-hidden">Configuration snapshots</caption>
              <thead><tr><th scope="col" class="num">Version</th><th scope="col">Label</th><th scope="col">Kind</th><th scope="col" class="hide-sm">Captured</th><th scope="col" class="hide-sm">Items</th><th scope="col"><span class="sd-visually-hidden">Actions</span></th></tr></thead>
              <tbody>
                @for (s of snapshots(); track s.id) {
                  <tr>
                    <td class="num"><strong>v{{ s.version }}</strong></td>
                    <td>{{ s.label }}</td>
                    <td><sd-badge size="sm" [tone]="kindTone(s.kind)">{{ kindLabel(s.kind) }}</sd-badge></td>
                    <td class="hide-sm">{{ s.createdAt | sdDateTime }}{{ s.createdByName ? ' · ' + s.createdByName : '' }}</td>
                    <td class="hide-sm">{{ itemCount(s) }}</td>
                    <td class="actions">
                      <button sdButton size="sm" variant="ghost" icon="download" iconOnly [attr.aria-label]="'Download v' + s.version" (click)="downloadSnapshot(s)"></button>
                      <button sdButton size="sm" icon="history" [disabled]="s.version === latest()" (click)="rollback(s)">Roll back</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </div>

    <sd-drawer [(open)]="draftOpen" [title]="draft()?.label ?? 'Import draft'" width="min(860px, 100vw)">
      @if (draft(); as d) {
        @if (d.diff; as diff) {
          <div class="summary">
            <sd-badge tone="success">{{ diff.new }} new</sd-badge>
            <sd-badge tone="warning">{{ diff.changed }} changed</sd-badge>
            <sd-badge>{{ diff.unchanged }} unchanged</sd-badge>
            <span class="sd-spacer"></span>
            <label class="sd-row small"><input type="checkbox" [checked]="hideUnchanged()" (change)="hideUnchanged.set($any($event.target).checked)" /> Hide unchanged</label>
          </div>
          @for (w of diff.warnings; track w) {
            <div class="callout callout--warning"><sd-icon name="triangle-alert" [size]="16" /><div class="grow">{{ w }}</div></div>
          }
          @for (s of diff.sections; track s.key) {
            @if (visible(s.items).length) {
              <h3 class="section-title">{{ s.label }}</h3>
              <ul class="items">
                @for (item of visible(s.items); track item.key) {
                  <li>
                    <div class="item-head">
                      <sd-badge size="sm" [tone]="statusTone(item.status)">{{ item.status }}</sd-badge>
                      <strong class="name">{{ item.name }}</strong>
                      @if (item.name !== item.naturalKey) {
                        <span class="muted small">{{ item.naturalKey }}</span>
                      }
                      <span class="sd-spacer"></span>
                      <label class="sd-visually-hidden" [attr.for]="'res-' + item.key">Resolution for {{ item.name }}</label>
                      <select sdInput size="sm" [id]="'res-' + item.key" [ngModel]="action(item)" (ngModelChange)="setAction(item, $event)">
                        @for (a of actionsFor(item); track a) {
                          <option [value]="a">{{ actionLabel(a) }}</option>
                        }
                      </select>
                    </div>
                    @if (action(item) === 'rename') {
                      <sd-form-field label="New name" [error]="renameErrors()[item.key]" inline>
                        <input sdInput size="sm" [ngModel]="newName(item)" (ngModelChange)="setName(item, $event)" />
                      </sd-form-field>
                    }
                    @if (item.changes.length && item.status === 'changed') {
                      <details>
                        <summary class="small">{{ item.changes.length }} field(s) differ</summary>
                        <div class="table-wrap">
                          <table class="data">
                            <caption class="sd-visually-hidden">Differences for {{ item.name }}</caption>
                            <thead><tr><th scope="col">Field</th><th scope="col">This workspace</th><th scope="col">File</th></tr></thead>
                            <tbody>
                              @for (c of item.changes; track c.field) {
                                <tr><td class="mono">{{ c.field }}</td><td class="mono before">{{ c.current ?? '—' }}</td><td class="mono after">{{ c.incoming ?? '—' }}</td></tr>
                              }
                            </tbody>
                          </table>
                        </div>
                      </details>
                    }
                  </li>
                }
              </ul>
            }
          }
        } @else {
          <sd-empty-state compact icon="file-text" [title]="'Draft ' + d.status.toLowerCase()" />
        }
        @if (draftError()) {
          <p class="err" role="alert">{{ draftError() }}</p>
        }
      } @else {
        <sd-skeleton [lines]="10" />
      }
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" [disabled]="!draft()" (click)="discard()">Discard draft</button>
        <span class="sd-spacer"></span>
        <button sdButton type="button" [disabled]="!dirty()" [loading]="savingResolutions()" (click)="saveResolutions()">Save choices</button>
        <button sdButton type="button" variant="primary" [disabled]="!draft()?.diff" [loading]="publishing()" (click)="publish()">Publish</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    ${SAAS_CSS}
    .grid { display: grid; gap: var(--sd-space-4); grid-template-columns: repeat(auto-fit, minmax(min(100%, 380px), 1fr)); }
    .checks { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(min(100%, 230px), 1fr)); margin-bottom: 12px; }
    .drop { position: relative; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; padding: 28px 16px;
      border: 2px dashed var(--sd-border-strong); border-radius: var(--sd-radius-lg); color: var(--sd-text-2); cursor: pointer; text-align: center; }
    .drop:hover, .drop:focus-within { border-color: var(--sd-primary); color: var(--sd-primary); }
    .drop input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .drop.busy { opacity: 0.6; }
    .list { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
    .list li { display: flex; align-items: center; gap: 8px; }
    .grow { flex: 1; min-width: 0; margin: 0; }
    .summary { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-bottom: 12px; }
    .items { list-style: none; margin: 0; padding: 0; display: grid; gap: 8px; }
    .items li { padding: 10px 12px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); display: grid; gap: 8px; }
    .item-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .item-head select { width: auto; }
    .name { overflow-wrap: anywhere; }
    details summary { cursor: pointer; color: var(--sd-text-2); }
    td.before { background: var(--sd-danger-bg); }
    td.after { background: var(--sd-success-bg); }
  `,
})
export class ConfigTransferPage implements OnInit {
  private readonly api = inject(ConfigTransferApi);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(SdDialogService);

  protected readonly crumbs = [{ label: 'Administration', link: '/admin' }, { label: 'Configuration transfer' }];
  protected readonly sections = signal<ConfigSectionInfoDto[] | null>(null);
  protected readonly chosen = signal<string[]>([]);
  protected readonly exporting = signal(false);
  protected readonly importing = signal(false);
  protected readonly importError = signal<string | null>(null);
  protected readonly drafts = signal<ConfigDraftDto[]>([]);
  protected readonly snapshots = signal<ConfigSnapshotDto[] | null>(null);
  protected readonly capturing = signal(false);
  protected readonly draftOpen = signal(false);
  protected readonly draft = signal<ConfigDraftDto | null>(null);
  protected readonly draftError = signal<string | null>(null);
  protected readonly hideUnchanged = signal(true);
  protected readonly choices = signal<Record<string, ConfigResolutionDto>>({});
  protected readonly renameErrors = signal<Record<string, string | null>>({});
  protected readonly savingResolutions = signal(false);
  protected readonly publishing = signal(false);
  protected readonly dirty = computed(() => Object.keys(this.choices()).length > 0);
  protected readonly latest = computed(() => this.snapshots()?.[0]?.version ?? 0);

  ngOnInit(): void {
    this.api.sections().subscribe({ next: (s) => this.sections.set(s), error: () => this.sections.set([]) });
    this.loadDrafts();
    this.loadSnapshots();
  }

  private loadDrafts(): void {
    this.api.drafts().subscribe({ next: (d) => this.drafts.set(d), error: () => undefined });
  }

  private loadSnapshots(): void {
    this.api.snapshots().subscribe({ next: (s) => this.snapshots.set(s), error: () => this.snapshots.set([]) });
  }

  protected toggle(key: string, on: boolean): void {
    this.chosen.set(on ? [...this.chosen(), key] : this.chosen().filter((k) => k !== key));
  }

  protected export(): void {
    this.exporting.set(true);
    this.api.export(this.chosen()).subscribe({
      next: (blob) => {
        saveBlob(blob, `supportdesk-config-${new Date().toISOString().slice(0, 10)}.json`);
        this.exporting.set(false);
      },
      error: (e: ApiError) => {
        this.exporting.set(false);
        this.toast.error('Export failed', e.userMessage);
      },
    });
  }

  protected async onFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.importError.set(null);
    if (file.size > 20 * 1024 * 1024) {
      this.importError.set('The file is larger than 20 MB.');
      return;
    }
    let bundle: JsonValue;
    try {
      bundle = JSON.parse(await file.text()) as JsonValue;
    } catch {
      this.importError.set('This file is not valid JSON.');
      return;
    }
    this.importing.set(true);
    this.api.import(bundle, `${file.name} (${new Date().toLocaleString()})`).subscribe({
      next: (d) => {
        this.importing.set(false);
        this.loadDrafts();
        this.showDraft(d);
      },
      error: (e: ApiError) => {
        this.importing.set(false);
        this.importError.set(e.userMessage);
      },
    });
  }

  protected openDraft(id: string): void {
    this.draft.set(null);
    this.draftOpen.set(true);
    this.api.draft(id).subscribe({ next: (d) => this.showDraft(d), error: (e: ApiError) => this.draftError.set(e.userMessage) });
  }

  private showDraft(d: ConfigDraftDto): void {
    this.choices.set({});
    this.renameErrors.set({});
    this.draftError.set(null);
    this.draft.set(d);
    this.draftOpen.set(true);
  }

  protected visible(items: ConfigDiffItemDto[]): ConfigDiffItemDto[] {
    return this.hideUnchanged() ? items.filter((i) => i.status !== 'unchanged' || this.choices()[i.key]) : items;
  }

  protected actionsFor(item: ConfigDiffItemDto): ConfigResolutionAction[] {
    const rename: ConfigResolutionAction[] = item.section === 'prompts' ? [] : ['rename'];
    if (item.status === 'new') return ['create', 'skip', ...rename];
    if (item.status === 'changed') return ['overwrite', 'skip', ...rename];
    return ['skip', ...rename];
  }

  protected actionLabel(a: ConfigResolutionAction): string {
    return { create: 'Create', overwrite: 'Overwrite', skip: 'Skip', rename: 'Import as copy (rename)' }[a];
  }

  protected action(item: ConfigDiffItemDto): ConfigResolutionAction {
    return this.choices()[item.key]?.action ?? item.resolution;
  }

  protected newName(item: ConfigDiffItemDto): string {
    return this.choices()[item.key]?.newName ?? item.newName ?? '';
  }

  protected setAction(item: ConfigDiffItemDto, action: ConfigResolutionAction): void {
    const suggested = action === 'rename' ? this.newName(item) || `${item.naturalKey.split(/[/:]/).pop()} (imported)` : null;
    this.choices.set({ ...this.choices(), [item.key]: { key: item.key, action, newName: suggested } });
  }

  protected setName(item: ConfigDiffItemDto, name: string): void {
    this.choices.set({ ...this.choices(), [item.key]: { key: item.key, action: 'rename', newName: name } });
  }

  protected saveResolutions(onDone?: () => void): void {
    const d = this.draft();
    if (!d) return;
    const resolutions = Object.values(this.choices());
    if (resolutions.length === 0) {
      onDone?.();
      return;
    }
    this.savingResolutions.set(true);
    this.api.saveResolutions(d.id, resolutions).subscribe({
      next: (next) => {
        this.savingResolutions.set(false);
        this.choices.set({});
        this.renameErrors.set({});
        this.draft.set(next);
        onDone?.();
      },
      error: (e: ApiError) => {
        this.savingResolutions.set(false);
        const map: Record<string, string | null> = {};
        for (const k of Object.keys(e.errors ?? {})) map[k] = e.fieldError(k);
        this.renameErrors.set(map);
        this.draftError.set(Object.keys(map).length ? 'Some choices are invalid.' : e.userMessage);
      },
    });
  }

  protected async publish(): Promise<void> {
    const d = this.draft();
    if (!d?.diff) return;
    const ok = await this.dialog.confirm({
      title: 'Publish this configuration?',
      message: 'The chosen items are created or overwritten now. The result is recorded as a new snapshot you can roll back.',
      confirmLabel: 'Publish',
    });
    if (!ok) return;
    this.saveResolutions(() => {
      this.publishing.set(true);
      this.api.publish(d.id).subscribe({
        next: (r) => {
          this.publishing.set(false);
          this.draftOpen.set(false);
          this.toast.success(`Published as v${r.snapshot.version}`, `${r.result.created} created, ${r.result.updated} updated, ${r.result.skipped} skipped.`);
          if (r.result.warnings.length) this.toast.warning('Some references were not found', r.result.warnings.slice(0, 3).join(' '));
          this.loadDrafts();
          this.loadSnapshots();
          this.api.sections().subscribe((s) => this.sections.set(s));
        },
        error: (e: ApiError) => {
          this.publishing.set(false);
          this.draftError.set(e.userMessage);
        },
      });
    });
  }

  protected async discard(): Promise<void> {
    const d = this.draft();
    if (!d) return;
    if (!(await this.dialog.confirm({ title: 'Discard this draft?', danger: true, confirmLabel: 'Discard' }))) return;
    this.api.discard(d.id).subscribe(() => {
      this.draftOpen.set(false);
      this.loadDrafts();
    });
  }

  protected capture(): void {
    this.capturing.set(true);
    this.api.captureSnapshot(null).subscribe({
      next: (s) => {
        this.capturing.set(false);
        this.toast.success(`Snapshot v${s.version} captured`);
        this.loadSnapshots();
      },
      error: () => this.capturing.set(false),
    });
  }

  protected downloadSnapshot(s: ConfigSnapshotDto): void {
    this.api.downloadSnapshot(s.id).subscribe((blob) => saveBlob(blob, `supportdesk-config-v${s.version}.json`));
  }

  protected async rollback(s: ConfigSnapshotDto): Promise<void> {
    const ok = await this.dialog.confirm({
      title: `Roll back to v${s.version}?`,
      message: `“${s.label}” is restored: items are recreated or reverted, and items added since are deactivated (never deleted). The current state is captured first.`,
      confirmLabel: `Roll back to v${s.version}`,
      danger: true,
      typeToConfirm: `v${s.version}`,
    });
    if (!ok) return;
    this.api.rollback(s.id).subscribe({
      next: (r) => {
        this.toast.success(`Rolled back — now v${r.snapshot.version}`, `${r.result.created} restored, ${r.result.updated} reverted, ${r.result.deactivated} deactivated.`);
        this.loadSnapshots();
        this.api.sections().subscribe((x) => this.sections.set(x));
      },
      error: (e: ApiError) => this.toast.error('Rollback failed', e.userMessage),
    });
  }

  protected statusTone(status: string): SdTone {
    return status === 'new' ? 'success' : status === 'changed' ? 'warning' : 'neutral';
  }

  protected kindTone(kind: string): SdTone {
    return kind === 'Rollback' ? 'danger' : kind === 'Import' || kind === 'Publish' ? 'primary' : kind === 'PreRollback' ? 'warning' : 'neutral';
  }

  protected kindLabel(kind: string): string {
    return kind === 'PreRollback' ? 'Before rollback' : kind;
  }

  protected itemCount(s: ConfigSnapshotDto): number {
    return s.sections.reduce((sum, x) => sum + x.count, 0);
  }
}
