import { ChangeDetectionStrategy, Component, booleanAttribute, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ArticleTranslationDto, KnowledgeApi, ToastBridge, TranslationTransitionAction } from '@sd/core';
import {
  SdBadge,
  SdButton,
  SdCheckbox,
  SdDialogService,
  SdEmptyState,
  SdFormField,
  SdIcon,
  SdImageUploader,
  SdInput,
  SdRelativeTimePipe,
  SdRichTextEditor,
  SdSkeleton,
  isHtmlEmpty,
} from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbTranslationStatusBadge } from '../components/kb-badges.component';
import { KB_STYLES, errorMessage, localeLabel, remote, toastedGlobally } from '../kb-common';
import { KbStore } from '../kb.store';

interface TranslationDraft {
  locale: string;
  isNew: boolean;
  title: string;
  summary: string;
  bodyHtml: string;
  machineTranslated: boolean;
}

interface WorkflowButton {
  action: TranslationTransitionAction;
  label: string;
  icon: string;
}

const START: WorkflowButton = { action: 'start', label: 'Start translating', icon: 'languages' };
const SUBMIT: WorkflowButton = { action: 'submit', label: 'Submit for review', icon: 'send' };
const PUBLISH: WorkflowButton = { action: 'publish', label: 'Publish', icon: 'globe' };
const REOPEN: WorkflowButton = { action: 'reopen', label: 'Reopen', icon: 'archive-restore' };

/**
 * Translations of an article: add a locale, edit title/summary/body, move through the translation
 * workflow (start → submit → publish, reopen) and delete. Outdated translations are flagged.
 */
@Component({
  selector: 'desk-kb-article-translations',
  imports: [
    FormsModule,
    SdButton,
    SdBadge,
    SdIcon,
    SdInput,
    SdFormField,
    SdCheckbox,
    SdRichTextEditor,
    SdSkeleton,
    SdEmptyState,
    SdRelativeTimePipe,
    KbTranslationStatusBadge,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (canManage() && !editing()) {
      <div class="add">
        <label for="kb-add-locale" class="sd-small">Add a translation</label>
        <select sdInput id="kb-add-locale" size="sm" [ngModel]="newLocale()" (ngModelChange)="newLocale.set($event)" [disabled]="availableLocales().length === 0">
          @if (availableLocales().length === 0) {
            <option value="">All languages are covered</option>
          } @else {
            <option value="">Choose a language…</option>
          }
          @for (l of availableLocales(); track l) {
            <option [value]="l">{{ localeName(l) }}</option>
          }
        </select>
        <button sdButton size="sm" variant="subtle" icon="plus" type="button" [disabled]="!newLocale()" (click)="startNew()">Add</button>
      </div>
    }

    @if (editing(); as d) {
      <section class="editor" [attr.aria-label]="'Translation ' + localeName(d.locale)">
        <header class="editor-head">
          <h3><sd-icon name="languages" [size]="16" /> {{ localeName(d.locale) }}</h3>
          @if (d.isNew) {
            <sd-badge tone="info" size="sm">New</sd-badge>
          }
        </header>
        @if (d.isNew) {
          <p class="sd-small sd-secondary">Prefilled with the {{ localeName(primaryLocale()) }} text — replace it with the translation.</p>
        }
        <sd-form-field label="Title" required [error]="errors()['title']">
          <input sdInput maxlength="250" [ngModel]="d.title" (ngModelChange)="patch({ title: $event })" />
        </sd-form-field>
        <sd-form-field label="Summary" optional [error]="errors()['summary']">
          <textarea sdInput rows="2" maxlength="1000" [ngModel]="d.summary" (ngModelChange)="patch({ summary: $event })"></textarea>
        </sd-form-field>
        <sd-form-field label="Body" required [error]="errors()['bodyHtml']">
          <sd-rich-text-editor
            ariaLabel="Translated body"
            minHeight="220px"
            maxHeight="560px"
            [uploadImage]="uploadImage()"
            [value]="d.bodyHtml"
            (valueChange)="patch({ bodyHtml: $event })"
          />
        </sd-form-field>
        <sd-checkbox [value]="d.machineTranslated" (valueChange)="patch({ machineTranslated: $event })">Machine translated (flag for human review)</sd-checkbox>
        <details class="source">
          <summary>Show the source text ({{ localeName(primaryLocale()) }})</summary>
          <h4>{{ sourceTitle() }}</h4>
          @if (sourceSummary()) {
            <p class="sd-secondary">{{ sourceSummary() }}</p>
          }
          <div class="sd-prose" [innerHTML]="sourceBody()"></div>
        </details>
        <div class="editor-actions">
          <button sdButton type="button" (click)="cancel()" [disabled]="saving()">Cancel</button>
          <button sdButton variant="primary" icon="save" type="button" [loading]="saving()" (click)="save()">Save translation</button>
        </div>
      </section>
    }

    @if (translations.loading() && !translations.value()) {
      <div class="loading" aria-busy="true" aria-label="Loading translations">
        <sd-skeleton height="44px" radius="var(--sd-radius-sm)" />
        <sd-skeleton height="44px" radius="var(--sd-radius-sm)" />
      </div>
    } @else if (translations.error(); as err) {
      <div class="kb-error" role="alert">
        <sd-icon name="circle-alert" [size]="16" />
        <p>Could not load translations. {{ err }}</p>
        <button sdButton size="sm" type="button" icon="refresh" (click)="translations.reload()">Retry</button>
      </div>
    } @else if (list().length === 0 && !editing()) {
      <sd-empty-state icon="languages" title="No translations yet" [message]="'This article is only available in ' + localeName(primaryLocale()) + '.'" compact />
    } @else if (list().length) {
      <ul class="list" aria-label="Translations">
        @for (t of list(); track t.id) {
          <li class="item" [class.is-editing]="editing()?.locale === t.locale">
            <div class="item-main">
              <div class="item-title">
                <strong>{{ localeName(t.locale) }}</strong>
                <desk-kb-translation-status [status]="t.status" />
                @if (t.isOutdated) {
                  <sd-badge tone="warning" size="sm" icon="triangle-alert" [attr.title]="'Translated from version ' + t.sourceVersionNumber + '; the source has changed since.'">
                    Outdated
                  </sd-badge>
                }
                @if (t.machineTranslated) {
                  <sd-badge size="sm" variant="outline" icon="sparkles">Machine</sd-badge>
                }
              </div>
              <span class="sd-truncate">{{ t.title }}</span>
              <span class="kb-meta">
                <span>Source v{{ t.sourceVersionNumber }}</span>
                @if (t.translatorName) {
                  <span><sd-icon name="user" [size]="12" /> {{ t.translatorName }}</span>
                }
                <span>Updated {{ t.updatedAt | sdRelativeTime }}</span>
              </span>
            </div>
            @if (canManage()) {
              <div class="item-actions">
                <button sdButton size="sm" variant="ghost" icon="pencil" type="button" [disabled]="!!editing()" (click)="edit(t)">Edit</button>
                @for (b of buttonsFor(t); track b.action) {
                  <button sdButton size="sm" variant="ghost" [icon]="b.icon" type="button" [loading]="busy() === t.locale + ':' + b.action" (click)="transition(t, b.action)">
                    {{ b.label }}
                  </button>
                }
                <button
                  sdButton
                  size="sm"
                  variant="ghost"
                  icon="trash"
                  iconOnly
                  type="button"
                  [loading]="busy() === t.locale + ':delete'"
                  [attr.aria-label]="'Delete the ' + localeName(t.locale) + ' translation'"
                  (click)="remove(t)"
                ></button>
              </div>
            }
          </li>
        }
      </ul>
    }
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: grid;
        gap: var(--sd-space-3);
      }
      .add {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .add select {
        width: auto;
        min-width: 220px;
      }
      .loading {
        display: grid;
        gap: 6px;
      }
      .editor {
        display: grid;
        gap: var(--sd-space-3);
        padding: var(--sd-space-4);
        border: 1px solid var(--sd-primary-border);
        border-radius: var(--sd-radius-lg);
        background: var(--sd-surface);
        box-shadow: var(--sd-shadow-sm);
      }
      .editor-head {
        display: flex;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .editor-head h3 {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: var(--sd-text-lg);
      }
      .editor-actions {
        display: flex;
        justify-content: flex-end;
        gap: var(--sd-space-2);
      }
      .source {
        padding: var(--sd-space-2) var(--sd-space-3);
        border: 1px dashed var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface-2);
      }
      .source summary {
        cursor: pointer;
        color: var(--sd-text-2);
        font-size: var(--sd-text-sm);
        font-weight: var(--sd-weight-medium);
      }
      .source h4 {
        margin: var(--sd-space-2) 0 4px;
      }
      .list {
        display: grid;
        gap: 6px;
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .item {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2) var(--sd-space-3);
        padding: var(--sd-space-3);
        border: 1px solid var(--sd-border);
        border-radius: var(--sd-radius-md);
        background: var(--sd-surface);
      }
      .item.is-editing {
        border-color: var(--sd-primary-border);
      }
      .item-main {
        display: grid;
        gap: 4px;
        flex: 1 1 280px;
        min-width: 0;
      }
      .item-title {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 6px;
      }
      .item-actions {
        display: flex;
        flex-wrap: wrap;
        gap: 2px;
      }
    `,
  ],
})
export class ArticleTranslations {
  readonly articleId = input.required<string>();
  readonly primaryLocale = input.required<string>();
  readonly revision = input<string>('');
  readonly canManage = input(false, { transform: booleanAttribute });
  readonly canPublish = input(false, { transform: booleanAttribute });
  readonly sourceTitle = input('');
  readonly sourceSummary = input<string | null>(null);
  readonly sourceBody = input('');
  readonly uploadImage = input<SdImageUploader | null>(null);
  readonly countChange = output<number>();

  private readonly api = inject(KnowledgeApi);
  private readonly store = inject(KbStore);
  private readonly dialogs = inject(SdDialogService);
  private readonly toasts = inject(ToastBridge);

  protected readonly translations = remote<ArticleTranslationDto[]>();
  protected readonly list = computed(() => [...(this.translations.value() ?? [])].sort((a, b) => a.locale.localeCompare(b.locale)));
  protected readonly editing = signal<TranslationDraft | null>(null);
  protected readonly errors = signal<Record<string, string | null>>({});
  protected readonly saving = signal(false);
  protected readonly busy = signal<string | null>(null);
  protected readonly newLocale = signal('');
  protected readonly availableLocales = computed(() => {
    const used = new Set(this.list().map((t) => t.locale.toLowerCase()));
    const primary = this.primaryLocale().toLowerCase();
    return this.store.options().locales.filter((l) => l.toLowerCase() !== primary && !used.has(l.toLowerCase()));
  });

  constructor() {
    this.store.ensureOptions();
    effect(() => {
      const id = this.articleId();
      this.revision();
      untracked(() => this.translations.load(() => this.api.translations(id), { keepValue: true }));
    });
    effect(() => {
      const value = this.translations.value();
      if (value) untracked(() => this.countChange.emit(value.length));
    });
  }

  protected localeName(tag: string): string {
    return localeLabel(tag);
  }

  protected buttonsFor(t: ArticleTranslationDto): WorkflowButton[] {
    switch (t.status) {
      case 'Draft':
        return [START, SUBMIT];
      case 'InTranslation':
        return [SUBMIT];
      case 'InReview':
        return this.canPublish() ? [PUBLISH, REOPEN] : [REOPEN];
      case 'Published':
        return [REOPEN];
      case 'Outdated':
        return [START, REOPEN];
      default:
        return [];
    }
  }

  protected startNew(): void {
    const locale = this.newLocale();
    if (!locale) return;
    this.errors.set({});
    this.editing.set({
      locale,
      isNew: true,
      title: this.sourceTitle(),
      summary: this.sourceSummary() ?? '',
      bodyHtml: this.sourceBody(),
      machineTranslated: false,
    });
    this.newLocale.set('');
  }

  protected edit(t: ArticleTranslationDto): void {
    this.errors.set({});
    this.editing.set({ locale: t.locale, isNew: false, title: t.title, summary: t.summary ?? '', bodyHtml: t.bodyHtml, machineTranslated: t.machineTranslated });
  }

  protected patch(changes: Partial<TranslationDraft>): void {
    this.editing.update((d) => (d ? { ...d, ...changes } : d));
  }

  protected cancel(): void {
    this.editing.set(null);
    this.errors.set({});
  }

  protected async save(): Promise<void> {
    const d = this.editing();
    if (!d) return;
    const errors: Record<string, string | null> = {
      title: d.title.trim() ? null : 'Enter the translated title.',
      bodyHtml: isHtmlEmpty(d.bodyHtml) ? 'Enter the translated body.' : null,
    };
    this.errors.set(errors);
    if (Object.values(errors).some(Boolean)) return;
    this.saving.set(true);
    try {
      const saved = await firstValueFrom(
        this.api.saveTranslation(this.articleId(), d.locale, {
          title: d.title.trim(),
          summary: d.summary.trim() || null,
          bodyHtml: d.bodyHtml,
          machineTranslated: d.machineTranslated,
        }),
      );
      this.upsert(saved);
      this.editing.set(null);
      this.toasts.success('Translation saved', localeLabel(saved.locale));
    } catch (err) {
      const e = ApiError.from(err);
      this.errors.set({ title: e.fieldError('title'), summary: e.fieldError('summary'), bodyHtml: e.fieldError('bodyHtml') });
      this.toasts.error('Could not save the translation', e.userMessage);
    } finally {
      this.saving.set(false);
    }
  }

  protected async transition(t: ArticleTranslationDto, action: TranslationTransitionAction): Promise<void> {
    this.busy.set(`${t.locale}:${action}`);
    try {
      const updated = await firstValueFrom(this.api.transitionTranslation(this.articleId(), t.locale, action));
      this.upsert(updated);
      this.toasts.success(`${localeLabel(t.locale)}: ${updated.status === 'InTranslation' ? 'in translation' : updated.status === 'InReview' ? 'in review' : updated.status.toLowerCase()}`);
    } catch (err) {
      this.toasts.error('Could not change the translation status', errorMessage(err));
    } finally {
      this.busy.set(null);
    }
  }

  protected async remove(t: ArticleTranslationDto): Promise<void> {
    const ok = await this.dialogs.confirm({
      title: `Delete the ${localeLabel(t.locale)} translation?`,
      message: 'Readers of that language will see the primary-language article instead.',
      confirmLabel: 'Delete translation',
      danger: true,
    });
    if (!ok) return;
    this.busy.set(`${t.locale}:delete`);
    try {
      await firstValueFrom(this.api.deleteTranslation(this.articleId(), t.locale));
      this.translations.set((this.translations.value() ?? []).filter((x) => x.id !== t.id));
      if (this.editing()?.locale === t.locale) this.editing.set(null);
      this.toasts.success('Translation deleted');
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not delete the translation', errorMessage(err));
    } finally {
      this.busy.set(null);
    }
  }

  private upsert(t: ArticleTranslationDto): void {
    const list = this.translations.value() ?? [];
    const exists = list.some((x) => x.locale === t.locale);
    this.translations.set(exists ? list.map((x) => (x.locale === t.locale ? t : x)) : [...list, t]);
  }
}
