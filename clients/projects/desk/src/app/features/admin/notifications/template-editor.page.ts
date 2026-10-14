import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnInit, computed, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime, switchMap, catchError, of } from 'rxjs';
import { AdminNotificationsApi, ApiError, MetaApi, NotificationTemplateDto, TemplatePreviewDto } from '@sd/core';
import { SdBadge, SdButton, SdDialogService, SdEmptyState, SdFormField, SdIcon, SdInput, SdSkeleton, SdToggle, ToastService } from '@sd/ui';
import { ADMIN_EDITOR_STYLES, AdminLeaveGuarded, sameValue, saveErrors, warnOnUnload } from '../admin-shared';

interface TemplateForm {
  subject: string;
  bodyHtml: string;
  isActive: boolean;
}

/**
 * E-mail template editor: subject and HTML body with {{placeholders}} (click to insert at the cursor), a live preview
 * rendered by the API with sample values (sanitised, brand footer included), activate/deactivate and reset to default.
 */
@Component({
  selector: 'desk-admin-template-editor',
  imports: [FormsModule, RouterLink, SdButton, SdBadge, SdIcon, SdFormField, SdInput, SdToggle, SdEmptyState, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (loading()) {
      <sd-skeleton [lines]="8" height="40px" />
    } @else if (loadError()) {
      <sd-empty-state icon="circle-alert" title="The template could not be loaded" [message]="loadError()">
        <a sdButton routerLink="../" icon="arrow-left">Back to templates</a>
      </sd-empty-state>
    } @else if (template(); as t) {
      <div class="head">
        <a sdButton variant="ghost" size="sm" icon="arrow-left" routerLink="../">All templates</a>
        <h2>{{ t.name }}</h2>
        <sd-badge size="sm" variant="outline">{{ t.audience === 'Customer' ? 'To customers' : 'To agents' }}</sd-badge>
        @if (t.isCustomized) {
          <sd-badge size="sm" [tone]="t.isActive ? 'primary' : 'warning'">{{ t.isActive ? 'Customised' : 'Custom (inactive)' }}</sd-badge>
        } @else {
          <sd-badge size="sm">Default</sd-badge>
        }
      </div>
      <p class="sd-muted desc">{{ t.description }}</p>
      @if (formError()) {
        <div class="callout danger" role="alert"><sd-icon name="circle-alert" [size]="16" /><div>{{ formError() }}</div></div>
      }

      <div class="split">
        <section class="card" aria-label="Template">
          <div class="form">
            <sd-form-field label="Subject" required [error]="errors()['subject']">
              <input sdInput name="subject" [ngModel]="form().subject" (ngModelChange)="patch({ subject: $event })" maxlength="500" />
            </sd-form-field>
            <sd-form-field label="Body (HTML)" required [error]="errors()['bodyHtml']" hint="Values are inserted HTML-encoded; scripts and unsafe markup are removed when the e-mail is rendered.">
              <textarea #body sdInput name="body" class="mono code" rows="14" [ngModel]="form().bodyHtml" (ngModelChange)="patch({ bodyHtml: $event })"></textarea>
            </sd-form-field>
            <div class="ph" role="group" aria-label="Insert a placeholder">
              <span class="sd-muted small">Insert:</span>
              @for (p of t.placeholders; track p.name) {
                <button type="button" class="chip" [title]="p.description + ' — e.g. ' + p.sample" (click)="insert(p.name)">{{ token(p.name) }}</button>
              }
            </div>
            <sd-toggle [value]="form().isActive" (valueChange)="patch({ isActive: $event })">Use this custom template (off = built-in default)</sd-toggle>
          </div>
        </section>

        <section class="card preview" aria-labelledby="pv-h">
          <div class="pvhead">
            <h3 id="pv-h">Preview</h3>
            <span class="sd-spacer"></span>
            @if (brands().length > 1 && t.audience === 'Customer') {
              <select sdInput size="sm" aria-label="Preview for brand" [ngModel]="brandId()" (ngModelChange)="brandId.set($event); refreshPreview()">
                @for (b of brands(); track b.id) {
                  <option [value]="b.id">{{ b.name }}</option>
                }
              </select>
            }
          </div>
          @if (preview(); as p) {
            @if (p.unknownPlaceholders.length) {
              <div class="callout warn" role="status"><sd-icon name="triangle-alert" [size]="16" /><div>Unknown placeholders: {{ p.unknownPlaceholders.join(', ') }} — they would render empty.</div></div>
            }
            <div class="mail">
              <div class="mhead"><span class="sd-muted small">Subject</span><strong>{{ p.subject }}</strong></div>
              <div class="mbody sd-prose" [innerHTML]="p.bodyHtml"></div>
            </div>
          } @else if (previewError()) {
            <p class="err">{{ previewError() }}</p>
          } @else {
            <sd-skeleton [lines]="5" />
          }
          <p class="sd-muted small">Sample values are used for placeholders.</p>
        </section>
      </div>

      <div class="savebar">
        @if (t.isCustomized) {
          <button sdButton variant="ghost" icon="history" (click)="reset()">Reset to default</button>
        }
        @if (dirty()) {
          <span class="sd-muted small"><sd-icon name="circle-dot" [size]="12" /> Unsaved changes</span>
        }
        <span class="sd-spacer"></span>
        <button sdButton variant="ghost" [disabled]="!dirty() || saving()" (click)="discard()">Discard</button>
        <button sdButton variant="primary" icon="check" [loading]="saving()" [disabled]="!dirty()" (click)="save()">Save template</button>
      </div>
    }
  `,
  styles: `
    ${ADMIN_EDITOR_STYLES}
    .head { display: flex; flex-wrap: wrap; align-items: center; gap: var(--sd-space-2); }
    .head h2 { margin: 0; font-size: var(--sd-text-lg); }
    .desc { margin: 6px 0 var(--sd-space-4); font-size: var(--sd-text-sm); }
    .split { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: var(--sd-space-4); align-items: start; }
    .split .card + .card { margin-top: 0; }
    .code { min-height: 260px; line-height: 1.5; font-size: var(--sd-text-xs); }
    .ph { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .chip { padding: 2px 8px; border: 1px solid var(--sd-border); border-radius: var(--sd-radius-pill); background: var(--sd-surface-2); color: var(--sd-text); font-family: var(--sd-font-mono); font-size: var(--sd-text-xs); cursor: pointer; }
    .chip:hover { border-color: var(--sd-primary-border); background: var(--sd-primary-soft); }
    .chip:focus-visible { outline: none; box-shadow: var(--sd-focus-ring); }
    .pvhead { display: flex; align-items: center; gap: 8px; margin-bottom: var(--sd-space-3); }
    .pvhead h3 { margin: 0; font-size: var(--sd-text-md); }
    .pvhead select { width: auto; }
    .mail { border: 1px solid var(--sd-border); border-radius: var(--sd-radius-md); overflow: hidden; }
    .mhead { display: flex; flex-direction: column; gap: 2px; padding: 10px 12px; border-bottom: 1px solid var(--sd-border); background: var(--sd-surface-2); }
    .mbody { padding: 12px; background: var(--sd-surface); overflow-wrap: anywhere; }
    .callout { margin-bottom: var(--sd-space-3); }
    @media (max-width: 1000px) { .split { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class TemplateEditorPage implements OnInit, AdminLeaveGuarded {
  private readonly api = inject(AdminNotificationsApi);
  private readonly meta = inject(MetaApi);
  private readonly toast = inject(ToastService);
  private readonly dialogs = inject(SdDialogService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly bodyRef = viewChild<ElementRef<HTMLTextAreaElement>>('body');

  /** Route param: the template key. */
  readonly key = input.required<string>();

  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly template = signal<NotificationTemplateDto | null>(null);
  protected readonly form = signal<TemplateForm>({ subject: '', bodyHtml: '', isActive: true });
  private readonly original = signal<TemplateForm | null>(null);
  protected readonly preview = signal<TemplatePreviewDto | null>(null);
  protected readonly previewError = signal<string | null>(null);
  protected readonly brandId = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  private readonly previewRequests = new Subject<void>();

  protected readonly brands = computed(() => this.meta.ticketForm()?.brands ?? []);
  protected readonly dirty = computed(() => !!this.original() && !sameValue(this.form(), this.original()));

  constructor() {
    warnOnUnload(this.dirty);
    const sub = this.previewRequests
      .pipe(
        debounceTime(400),
        switchMap(() => {
          const f = this.form();
          return this.api.preview(this.key(), { subject: f.subject, bodyHtml: f.bodyHtml, brandId: this.brandId() }).pipe(
            catchError((err: unknown) => {
              this.previewError.set(ApiError.from(err).userMessage);
              return of(null);
            }),
          );
        }),
      )
      .subscribe((p) => {
        if (p) {
          this.preview.set(p);
          this.previewError.set(null);
        }
      });
    this.destroyRef.onDestroy(() => sub.unsubscribe());
  }

  ngOnInit(): void {
    this.meta.load();
    this.api.template(this.key()).subscribe({
      next: (t) => {
        this.apply(t);
        this.loading.set(false);
        this.refreshPreview();
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

  protected patch(change: Partial<TemplateForm>): void {
    this.form.update((f) => ({ ...f, ...change }));
    if ('subject' in change || 'bodyHtml' in change) this.refreshPreview();
  }

  protected refreshPreview(): void {
    this.previewRequests.next();
  }

  protected token(name: string): string {
    return `{{${name}}}`;
  }

  /** Inserts the placeholder token at the cursor of the body (or appends it). */
  protected insert(name: string): void {
    const token = this.token(name);
    const el = this.bodyRef()?.nativeElement;
    const body = this.form().bodyHtml;
    if (!el) {
      this.patch({ bodyHtml: body + token });
      return;
    }
    const start = el.selectionStart ?? body.length;
    const end = el.selectionEnd ?? body.length;
    this.patch({ bodyHtml: body.slice(0, start) + token + body.slice(end) });
    queueMicrotask(() => {
      el.focus();
      el.setSelectionRange(start + token.length, start + token.length);
    });
  }

  protected discard(): void {
    const o = this.original();
    if (o) this.form.set({ ...o });
    this.errors.set({});
    this.formError.set(null);
    this.refreshPreview();
  }

  protected save(): void {
    const t = this.template();
    if (!t) return;
    const f = this.form();
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    this.api.saveTemplate(t.key, { subject: f.subject, bodyHtml: f.bodyHtml, isActive: f.isActive, rowVersion: t.rowVersion }).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.apply(saved);
        this.toast.success('Template saved', saved.name);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (ApiError.from(err).is('concurrency_conflict')) {
          this.formError.set('Someone else changed this template. Reload the page to see their version.');
          return;
        }
        const { fields, message } = saveErrors(err, 'The template could not be saved.');
        this.errors.set(fields);
        this.formError.set(message);
      },
    });
  }

  protected async reset(): Promise<void> {
    const t = this.template();
    if (!t) return;
    const ok = await this.dialogs.confirm({ title: 'Reset to the built-in template?', message: 'Your customised subject and body are deleted.', danger: true, confirmLabel: 'Reset' });
    if (!ok) return;
    this.api.resetTemplate(t.key).subscribe({
      next: (d) => {
        this.apply(d);
        this.refreshPreview();
        this.toast.success('Template reset to the default');
      },
      error: (err: unknown) => this.toast.error('Could not reset the template', ApiError.from(err).userMessage),
    });
  }

  private apply(t: NotificationTemplateDto): void {
    this.template.set(t);
    const f: TemplateForm = { subject: t.subject, bodyHtml: t.bodyHtml, isActive: t.isCustomized ? t.isActive : true };
    this.form.set(f);
    // A template that was never customised starts "clean" with its default text; saving creates the override.
    this.original.set({ ...f });
  }
}
