import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup, moveItemInArray, transferArrayItem } from '@angular/cdk/drag-drop';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import {
  AdminCustomFieldDto,
  AdminLayoutDto,
  AdminLayoutKind,
  ApiError,
  LayoutFieldOptionDto,
  LayoutFieldRef,
  MetaApi,
  SaveLayoutRequest,
  TicketConfigApi,
} from '@sd/core';
import { SdBadge, SdButton, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

interface BuilderSection {
  uid: number;
  title: string;
  columns: number;
  fields: LayoutFieldRef[];
}

interface LayoutMeta {
  name: string;
  kind: AdminLayoutKind;
  departmentId: string;
  brandId: string;
  channel: string;
  isDefault: boolean;
  isActive: boolean;
}

let uidSeq = 1;

/**
 * Drag-and-drop form builder: arrange standard and custom fields into sections (1–3 columns), mark fields required,
 * scope the layout to a department, brand (portal) or channel, and preview the form. Every action also works with the
 * keyboard (Add buttons and move up/down).
 */
@Component({
  selector: 'desk-admin-layout-builder',
  imports: [FormsModule, RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, CdkDragHandle, SdButton, SdBadge, SdIcon, SdFormField, SdInput, SdToggle, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="8" height="44px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="The layout could not be loaded" [message]="loadError()">
        <a sdButton routerLink="../" icon="arrow-left">Back to layouts</a>
      </sd-empty-state>
    } @else {
      <div class="head">
        <a sdButton variant="ghost" size="sm" icon="arrow-left" routerLink="../">All layouts</a>
        <h2>{{ layout() ? 'Edit layout' : 'New layout' }}</h2>
        <span class="sd-spacer"></span>
        <div class="seg" role="tablist" aria-label="Builder view">
          <button type="button" role="tab" [attr.aria-selected]="!preview()" [class.on]="!preview()" (click)="preview.set(false)"><sd-icon name="layers" [size]="14" /> Design</button>
          <button type="button" role="tab" [attr.aria-selected]="preview()" [class.on]="preview()" (click)="preview.set(true)"><sd-icon name="eye" [size]="14" /> Preview</button>
        </div>
      </div>
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }

      <section class="card settings" aria-label="Layout settings">
        <div class="grid3">
          <sd-form-field label="Name" required [error]="errors()['name']">
            <input sdInput name="name" [ngModel]="meta().name" (ngModelChange)="patchMeta({ name: $event })" maxlength="100" />
          </sd-form-field>
          <sd-form-field label="Form" required [error]="errors()['kind']">
            <select sdInput name="kind" [ngModel]="meta().kind" (ngModelChange)="setKind($event)">
              <option value="AgentForm">Agent ticket form</option>
              <option value="PortalForm">Portal (customer) form</option>
            </select>
          </sd-form-field>
          <sd-form-field label="Department" [error]="errors()['departmentId']">
            <select sdInput name="dept" [ngModel]="meta().departmentId" (ngModelChange)="patchMeta({ departmentId: $event })">
              <option value="">All departments</option>
              @for (d of departments(); track d.id) {
                <option [value]="d.id">{{ d.name }}</option>
              }
            </select>
          </sd-form-field>
          @if (meta().kind === 'PortalForm') {
            <sd-form-field label="Brand" [error]="errors()['brandId']">
              <select sdInput name="brand" [ngModel]="meta().brandId" (ngModelChange)="patchMeta({ brandId: $event })">
                <option value="">All brands</option>
                @for (b of brands(); track b.id) {
                  <option [value]="b.id">{{ b.name }}</option>
                }
              </select>
            </sd-form-field>
          } @else {
            <sd-form-field label="Channel" [error]="errors()['channel']">
              <select sdInput name="channel" [ngModel]="meta().channel" (ngModelChange)="patchMeta({ channel: $event })">
                <option value="">All channels</option>
                @for (c of channels(); track c) {
                  <option [value]="c">{{ c }}</option>
                }
              </select>
            </sd-form-field>
          }
          <div class="flags">
            <sd-toggle [value]="meta().isDefault" (valueChange)="patchMeta({ isDefault: $event })">Default {{ meta().kind === 'PortalForm' ? 'portal' : 'agent' }} form</sd-toggle>
            <sd-toggle [value]="meta().isActive" (valueChange)="patchMeta({ isActive: $event })">Active</sd-toggle>
          </div>
        </div>
      </section>

      @if (errors()['sections']) {
        <p class="err" role="alert">{{ errors()['sections'] }}</p>
      }

      @if (!preview()) {
        <div class="builder" cdkDropListGroup>
          <aside class="palette card" aria-labelledby="palette-h">
            <h3 id="palette-h">Available fields</h3>
            <input sdInput size="sm" type="search" placeholder="Filter fields" aria-label="Filter available fields" [ngModel]="paletteFilter()" (ngModelChange)="paletteFilter.set($event)" />
            <ul class="plist" cdkDropList id="palette" [cdkDropListData]="palette()" cdkDropListSortingDisabled (cdkDropListDropped)="dropToPalette($event)" aria-label="Available fields">
              @for (f of palette(); track f.key) {
                <li cdkDrag [cdkDragData]="f.key" class="pfield">
                  <sd-icon name="grip" [size]="14" class="g" />
                  <span class="grow">{{ f.label }} @if (f.custom) {<sd-badge size="sm" variant="outline">custom</sd-badge>}</span>
                  <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="plus" [attr.aria-label]="'Add ' + f.label + ' to the last section'" (click)="addToLast(f.key)"></button>
                </li>
              } @empty {
                <li class="sd-muted small none">Every field is placed.</li>
              }
            </ul>
          </aside>

          <div class="sections">
            @for (s of sections(); track s.uid; let si = $index; let lastSection = $last) {
              <section class="section card" [attr.aria-label]="'Section ' + (si + 1)">
                <div class="shead">
                  <input sdInput size="sm" class="stitle" [ngModel]="s.title" (ngModelChange)="patchSection(si, { title: $event })" placeholder="Section title" [attr.aria-label]="'Title of section ' + (si + 1)" maxlength="100" />
                  <select sdInput size="sm" class="cols" [ngModel]="s.columns" (ngModelChange)="patchSection(si, { columns: +$event })" [attr.aria-label]="'Columns of section ' + (si + 1)">
                    <option [ngValue]="1">1 column</option>
                    <option [ngValue]="2">2 columns</option>
                    <option [ngValue]="3">3 columns</option>
                  </select>
                  <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="si === 0" [attr.aria-label]="'Move section ' + (si + 1) + ' up'" (click)="moveSection(si, -1)"></button>
                  <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="lastSection" [attr.aria-label]="'Move section ' + (si + 1) + ' down'" (click)="moveSection(si, 1)"></button>
                  <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="trash" [attr.aria-label]="'Remove section ' + (si + 1)" (click)="removeSection(si)"></button>
                </div>
                <ol class="flist" cdkDropList [id]="'sec-' + s.uid" [cdkDropListData]="s.fields" (cdkDropListDropped)="drop($event, si)" [class.cols2]="s.columns === 2" [class.cols3]="s.columns === 3"
                  [attr.aria-label]="'Fields in section ' + (s.title || si + 1)">
                  @for (f of s.fields; track f.key; let fi = $index; let lastField = $last) {
                    <li cdkDrag [cdkDragData]="f.key" class="ffield">
                      <button type="button" class="grip" cdkDragHandle [attr.aria-label]="'Drag ' + label(f.key)"><sd-icon name="grip" [size]="14" /></button>
                      <span class="grow fl">{{ label(f.key) }}</span>
                      <label class="req" [class.locked]="alwaysRequired(f.key)">
                        <input type="checkbox" [checked]="f.required || alwaysRequired(f.key)" [disabled]="alwaysRequired(f.key)" (change)="setRequired(si, fi, $any($event.target).checked)" />
                        Required
                      </label>
                      <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-up" [disabled]="si === 0 && fi === 0" [attr.aria-label]="'Move ' + label(f.key) + ' up'" (click)="moveField(si, fi, -1)"></button>
                      <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="arrow-down" [disabled]="lastSection && lastField" [attr.aria-label]="'Move ' + label(f.key) + ' down'" (click)="moveField(si, fi, 1)"></button>
                      <button sdButton type="button" size="sm" variant="ghost" iconOnly icon="x" [attr.aria-label]="'Remove ' + label(f.key)" (click)="removeField(si, fi)"></button>
                    </li>
                  } @empty {
                    <li class="drop-hint">Drag fields here, or use + in the field list.</li>
                  }
                </ol>
              </section>
            }
            <button sdButton type="button" icon="plus" (click)="addSection()">Add section</button>
          </div>
        </div>
      } @else {
        <section class="card preview" aria-label="Form preview">
          <p class="sd-muted small">Preview of the {{ meta().kind === 'PortalForm' ? 'customer portal' : 'agent' }} form{{ meta().departmentId ? ' for ' + departmentName(meta().departmentId) : '' }}. Controls are inactive.</p>
          @for (s of sections(); track s.uid) {
            <fieldset class="psec">
              @if (s.title) {
                <legend>{{ s.title }}</legend>
              }
              <div class="pgrid" [style.grid-template-columns]="'repeat(' + s.columns + ', minmax(0, 1fr))'">
                @for (f of s.fields; track f.key) {
                  <label class="pf">
                    <span>{{ label(f.key) }}@if (f.required || alwaysRequired(f.key)) {<span class="star" aria-hidden="true"> *</span>}</span>
                    @switch (controlKind(f.key)) {
                      @case ('textarea') {
                        <textarea class="sd-input" rows="3" disabled [placeholder]="help(f.key)"></textarea>
                      }
                      @case ('select') {
                        <select class="sd-input" disabled>
                          <option>{{ previewOptions(f.key).at(0) ?? 'Select…' }}</option>
                        </select>
                      }
                      @case ('checkbox') {
                        <input type="checkbox" disabled />
                      }
                      @case ('date') {
                        <input class="sd-input" type="date" disabled />
                      }
                      @default {
                        <input class="sd-input" disabled [placeholder]="help(f.key)" />
                      }
                    }
                  </label>
                }
              </div>
            </fieldset>
          } @empty {
            <p class="sd-muted">Add sections and fields to see the form.</p>
          }
        </section>
      }

      <div class="savebar">
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        } @else {
          <span class="sd-muted small">All changes saved</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="layout() !== null && !dirty()" (click)="save()">{{ layout() ? 'Save layout' : 'Create layout' }}</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .head { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2); margin-bottom: var(--sd-space-3); }
    .head h2 { margin: 0; font-size: var(--sd-text-lg); }
    .seg { display: inline-flex; padding: 3px; border-radius: var(--sd-radius-md); background: var(--sd-surface-sunken); }
    .seg button { display: inline-flex; align-items: center; gap: 4px; padding: 5px 10px; border: 0; border-radius: var(--sd-radius-sm); background: none; color: var(--sd-text-2); font: inherit; cursor: pointer; }
    .seg button.on { background: var(--sd-surface); color: var(--sd-text); box-shadow: var(--sd-shadow-xs); }
    .seg button:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .settings { margin-bottom: var(--sd-space-4); }
    .flags { display: flex; flex-direction: column; gap: 8px; justify-content: center; }
    .builder { display: grid; grid-template-columns: 280px minmax(0, 1fr); gap: var(--sd-space-4); align-items: start; }
    .palette { position: sticky; top: 0; display: flex; flex-direction: column; gap: 8px; max-height: 70vh; padding: var(--sd-space-3); }
    .palette h3, .section h3 { margin: 0; font-size: var(--sd-text-sm); }
    .plist, .flist { margin: 0; padding: 0; list-style: none; }
    .plist { overflow: auto; display: flex; flex-direction: column; gap: 4px; min-height: 40px; }
    .pfield, .ffield { display: flex; align-items: center; gap: 6px; padding: 4px 6px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-sm); background: var(--sd-surface); font-size: var(--sd-text-sm); cursor: grab; }
    .pfield .g { color: var(--sd-text-3); }
    .none { padding: 6px; border: 0; }
    .sections { display: flex; flex-direction: column; gap: var(--sd-space-3); }
    .section { padding: var(--sd-space-3); }
    .section.card + .section.card { margin-top: 0; }
    .shead { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-bottom: 8px; }
    .stitle { flex: 1; min-width: 160px; font-weight: var(--sd-weight-semibold); }
    .cols { width: 120px; }
    .flist { display: grid; grid-template-columns: minmax(0, 1fr); gap: 6px; min-height: 44px; padding: 6px; border: 1px dashed var(--sd-border-strong); border-radius: var(--sd-radius-sm); }
    .flist.cols2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    .flist.cols3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }
    .fl { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .grip { display: grid; place-items: center; width: 20px; height: 24px; border: 0; background: none; color: var(--sd-text-3); cursor: grab; }
    .grip:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .req { display: inline-flex; align-items: center; gap: 3px; font-size: var(--sd-text-xs); color: var(--sd-text-2); white-space: nowrap; }
    .req.locked { opacity: 0.7; }
    .drop-hint { grid-column: 1 / -1; padding: 8px; color: var(--sd-text-3); font-size: var(--sd-text-sm); text-align: center; }
    .cdk-drag-preview { box-shadow: var(--sd-shadow-lg); list-style: none; display: flex; align-items: center; gap: 6px; padding: 4px 8px; border-radius: var(--sd-radius-sm); background: var(--sd-surface); font-size: var(--sd-text-sm); }
    .cdk-drag-placeholder { opacity: 0.35; }
    .preview { max-width: 900px; }
    .psec { margin-bottom: var(--sd-space-3); }
    .pgrid { display: grid; gap: var(--sd-space-3); }
    .pf { display: flex; flex-direction: column; gap: 4px; font-size: var(--sd-text-sm); font-weight: var(--sd-weight-medium); }
    .star { color: var(--sd-danger); }
    @media (max-width: 960px) {
      .builder { grid-template-columns: minmax(0, 1fr); }
      .palette { position: static; max-height: 280px; }
      .flist.cols2, .flist.cols3 { grid-template-columns: minmax(0, 1fr); }
      .pgrid { grid-template-columns: minmax(0, 1fr) !important; }
    }
  `,
})
export class LayoutBuilderPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(TicketConfigApi);
  private readonly metaApi = inject(MetaApi);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  /** Route param: a layout id, or "new". */
  readonly id = input<string | undefined>();

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly layout = signal<AdminLayoutDto | null>(null);
  protected readonly catalog = signal<LayoutFieldOptionDto[]>([]);
  private readonly customFields = signal<AdminCustomFieldDto[]>([]);
  protected readonly meta = signal<LayoutMeta>({ name: '', kind: 'AgentForm', departmentId: '', brandId: '', channel: '', isDefault: false, isActive: true });
  protected readonly sections = signal<BuilderSection[]>([]);
  private readonly original = signal<unknown>(null);
  protected readonly paletteFilter = signal('');
  protected readonly preview = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly departments = computed(() => this.metaApi.ticketForm()?.departments ?? []);
  protected readonly brands = computed(() => this.metaApi.ticketForm()?.brands ?? []);
  protected readonly channels = computed(() => this.metaApi.ticketForm()?.channels ?? []);
  private readonly byKey = computed(() => new Map(this.catalog().map((f) => [f.key, f])));
  protected readonly placed = computed(() => new Set(this.sections().flatMap((s) => s.fields.map((f) => f.key))));
  protected readonly palette = computed(() => {
    const kind = this.meta().kind;
    const q = this.paletteFilter().trim().toLowerCase();
    return this.catalog().filter(
      (f) => (kind === 'PortalForm' ? f.portalForm : f.agentForm) && !this.placed().has(f.key) && (!q || f.label.toLowerCase().includes(q)),
    );
  });
  protected readonly dirty = computed(() => !sameValue(this.snapshot(), this.original()));

  constructor() {
    warnOnUnload(this.dirty);
  }

  ngOnInit(): void {
    this.metaApi.load();
    const layoutId = this.id();
    forkJoin({
      catalog: this.api.layoutFields(),
      fields: this.api.fields('Ticket'),
      layout: layoutId && layoutId !== 'new' ? this.api.layout(layoutId) : of(null),
    }).subscribe({
      next: ({ catalog, fields, layout }) => {
        this.catalog.set(catalog);
        this.customFields.set(fields);
        if (layout) this.apply(layout);
        else {
          this.sections.set([
            { uid: uidSeq++, title: 'Request', columns: 1, fields: [{ key: 'subject', required: true }, { key: 'description', required: false }] },
            { uid: uidSeq++, title: 'Classification', columns: 2, fields: [{ key: 'departmentId', required: true }, { key: 'priority', required: false }] },
          ]);
          this.meta.set({ name: '', kind: 'AgentForm', departmentId: '', brandId: '', channel: '', isDefault: false, isActive: true });
          this.original.set(this.snapshot());
        }
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loadError.set(ApiError.from(err).userMessage);
        this.loading.set(false);
      },
    });
  }

  hasUnsavedChanges(): boolean {
    return this.dirty() && !this.saving();
  }

  protected patchMeta(change: Partial<LayoutMeta>): void {
    this.meta.update((m) => ({ ...m, ...change }));
  }

  /** Switching between agent and portal forms drops fields the other form cannot show. */
  protected setKind(kind: AdminLayoutKind): void {
    this.patchMeta({ kind, brandId: kind === 'PortalForm' ? this.meta().brandId : '', channel: kind === 'AgentForm' ? this.meta().channel : '' });
    const allowed = (key: string) => {
      const f = this.byKey().get(key);
      return !!f && (kind === 'PortalForm' ? f.portalForm : f.agentForm);
    };
    this.sections.update((list) => list.map((s) => ({ ...s, fields: s.fields.filter((f) => allowed(f.key)) })));
  }

  protected label(key: string): string {
    return this.byKey().get(key)?.label ?? key;
  }

  protected alwaysRequired(key: string): boolean {
    return this.byKey().get(key)?.alwaysRequired ?? false;
  }

  protected departmentName(id: string): string {
    return this.departments().find((d) => d.id === id)?.name ?? 'the department';
  }

  protected patchSection(index: number, change: Partial<BuilderSection>): void {
    this.sections.update((list) => list.map((s, i) => (i === index ? { ...s, ...change } : s)));
  }

  protected addSection(): void {
    this.sections.update((list) => [...list, { uid: uidSeq++, title: '', columns: 2, fields: [] }]);
  }

  protected removeSection(index: number): void {
    this.sections.update((list) => list.filter((_, i) => i !== index));
  }

  protected moveSection(index: number, delta: number): void {
    this.sections.update((list) => {
      const copy = [...list];
      moveItemInArray(copy, index, index + delta);
      return copy;
    });
  }

  protected addToLast(key: string): void {
    if (this.sections().length === 0) this.addSection();
    this.sections.update((list) => list.map((s, i) => (i === list.length - 1 ? { ...s, fields: [...s.fields, { key, required: false }] } : s)));
  }

  protected setRequired(si: number, fi: number, required: boolean): void {
    this.sections.update((list) => list.map((s, i) => (i === si ? { ...s, fields: s.fields.map((f, j) => (j === fi ? { ...f, required } : f)) } : s)));
  }

  protected removeField(si: number, fi: number): void {
    this.sections.update((list) => list.map((s, i) => (i === si ? { ...s, fields: s.fields.filter((_, j) => j !== fi) } : s)));
  }

  /** Keyboard move: up/down within a section, crossing into the neighbouring section at the edges. */
  protected moveField(si: number, fi: number, delta: number): void {
    this.sections.update((list) => {
      const copy = list.map((s) => ({ ...s, fields: [...s.fields] }));
      const field = copy[si].fields[fi];
      const target = fi + delta;
      if (target >= 0 && target < copy[si].fields.length) moveItemInArray(copy[si].fields, fi, target);
      else if (delta < 0 && si > 0) {
        copy[si].fields.splice(fi, 1);
        copy[si - 1].fields.push(field);
      } else if (delta > 0 && si < copy.length - 1) {
        copy[si].fields.splice(fi, 1);
        copy[si + 1].fields.unshift(field);
      }
      return copy;
    });
  }

  protected drop(event: CdkDragDrop<LayoutFieldRef[]>, si: number): void {
    this.sections.update((list) => {
      const copy = list.map((s) => ({ ...s, fields: [...s.fields] }));
      if (event.previousContainer.id === 'palette') {
        const key = event.item.data as string;
        if (!copy.some((s) => s.fields.some((f) => f.key === key))) copy[si].fields.splice(event.currentIndex, 0, { key, required: false });
      } else if (event.previousContainer === event.container) {
        moveItemInArray(copy[si].fields, event.previousIndex, event.currentIndex);
      } else {
        const from = copy.findIndex((s) => 'sec-' + s.uid === event.previousContainer.id);
        if (from >= 0) transferArrayItem(copy[from].fields, copy[si].fields, event.previousIndex, event.currentIndex);
      }
      return copy;
    });
  }

  /** Dropping a placed field back on the palette removes it from the layout. */
  protected dropToPalette(event: CdkDragDrop<LayoutFieldOptionDto[]>): void {
    if (event.previousContainer.id === 'palette') return;
    const key = event.item.data as string;
    this.sections.update((list) => list.map((s) => ({ ...s, fields: s.fields.filter((f) => f.key !== key) })));
  }

  protected controlKind(key: string): 'text' | 'textarea' | 'select' | 'checkbox' | 'date' {
    const t = this.byKey().get(key)?.type ?? 'Text';
    if (t === 'RichText' || t === 'TextArea') return 'textarea';
    if (t === 'Picklist' || t === 'MultiPicklist' || t === 'Lookup') return 'select';
    if (t === 'Checkbox') return 'checkbox';
    if (t === 'Date' || t === 'DateTime') return 'date';
    return 'text';
  }

  protected previewOptions(key: string): string[] {
    const m = this.metaApi.ticketForm();
    if (key === 'priority') return m?.priorities ?? [];
    if (key === 'severity') return m?.severities ?? [];
    if (key === 'channel') return m?.channels ?? [];
    if (key === 'departmentId') return (m?.departments ?? []).map((d) => d.name);
    if (key.startsWith('cf:')) {
      const def = this.customFields().find((f) => `cf:${f.key}` === key);
      if (def && Array.isArray(def.options))
        return def.options.map((o) => (o && typeof o === 'object' && !Array.isArray(o) ? String(o['label'] ?? o['value'] ?? '') : String(o)));
    }
    return [];
  }

  protected help(key: string): string {
    if (key.startsWith('cf:')) return this.customFields().find((f) => `cf:${f.key}` === key)?.helpText ?? '';
    return '';
  }

  protected discard(): void {
    const l = this.layout();
    if (l) this.apply(l);
    this.errors.set({});
    this.formError.set(null);
  }

  protected save(): void {
    const m = this.meta();
    const request: SaveLayoutRequest = {
      entity: 'Ticket',
      name: m.name.trim(),
      kind: m.kind,
      departmentId: m.departmentId || null,
      brandId: m.kind === 'PortalForm' ? m.brandId || null : null,
      channel: m.kind === 'AgentForm' ? m.channel || null : null,
      sections: this.sections().map((s) => ({ title: s.title.trim(), columns: s.columns, fields: s.fields })),
      isDefault: m.isDefault,
      isActive: m.isActive,
      rowVersion: this.layout()?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    const existing = this.layout();
    this.api.saveLayout(existing?.id ?? null, request).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.apply(saved);
        this.metaApi.refresh();
        this.toast.success(existing ? 'Layout saved' : 'Layout created', saved.name);
        if (!existing) void this.router.navigate(['/admin/ticket-config/layouts', saved.id], { replaceUrl: true });
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (ApiError.from(err).is('concurrency_conflict')) {
          this.formError.set('Someone else changed this layout. Reload the page to see their version.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The layout could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  private apply(l: AdminLayoutDto): void {
    this.layout.set(l);
    this.meta.set({
      name: l.name,
      kind: l.kind,
      departmentId: l.departmentId ?? '',
      brandId: l.brandId ?? '',
      channel: l.channel ?? '',
      isDefault: l.isDefault,
      isActive: l.isActive,
    });
    this.sections.set(
      (l.sections ?? []).map((s) => ({ uid: uidSeq++, title: s.title ?? '', columns: s.columns ?? 2, fields: (s.fields ?? []).map((f) => ({ key: f.key, required: !!f.required })) })),
    );
    this.original.set(this.snapshot());
  }

  private snapshot(): unknown {
    return { meta: this.meta(), sections: this.sections().map((s) => ({ t: s.title.trim(), c: s.columns, f: s.fields })) };
  }
}
