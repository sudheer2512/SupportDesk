import { ChangeDetectionStrategy, Component, booleanAttribute, computed, inject, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ArticleDetailDto, ArticleType, KbVisibility, KnowledgeApi } from '@sd/core';
import { SdBadge, SdChipsInput, SdCombobox, SdDateInput, SdDateTimePipe, SdFormField, SdIcon, SdInput, SdOption, SdOptionSource } from '@sd/ui';
import { map } from 'rxjs';
import { KbStatusBadge } from '../components/kb-badges.component';
import { ALL_ARTICLE_TYPES, ALL_VISIBILITIES, ARTICLE_TYPE_LABEL, VISIBILITY_META, comboMany, comboOne, localeLabel } from '../kb-common';
import { KbStore } from '../kb.store';
import { AccessRulesEditor } from './access-rules-editor.component';
import { ArticleDraft, ArticleErrors, RuleDraft } from './article-draft';

/**
 * Side panel of the article editor: status, classification, visibility + access rules, tags, related
 * articles, brands, product, owner, locale and review/expiry dates. Edits flow through the `draft` model.
 */
@Component({
  selector: 'desk-kb-article-properties',
  imports: [
    FormsModule,
    RouterLink,
    SdFormField,
    SdInput,
    SdCombobox,
    SdChipsInput,
    SdDateInput,
    SdBadge,
    SdIcon,
    SdDateTimePipe,
    KbStatusBadge,
    AccessRulesEditor,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel" aria-labelledby="kb-props-status">
      <h3 id="kb-props-status" class="panel-title">Status</h3>
      @if (article(); as a) {
        <div class="status-row">
          <desk-kb-status [status]="a.status" />
          @if (a.isLive) {
            <sd-badge tone="success" icon="globe">Live</sd-badge>
          } @else {
            <sd-badge variant="outline">Not live</sd-badge>
          }
        </div>
        <dl class="facts">
          <div>
            <dt>Working version</dt>
            <dd>v{{ a.currentVersionNumber }}</dd>
          </div>
          <div>
            <dt>Published version</dt>
            <dd>{{ a.publishedVersionNumber ? 'v' + a.publishedVersionNumber : '—' }}</dd>
          </div>
          @if (a.publishedAt) {
            <div>
              <dt>Published</dt>
              <dd>{{ a.publishedAt | sdDateTime: 'date' }}</dd>
            </div>
          }
          <div>
            <dt>Updated</dt>
            <dd>{{ a.updatedAt | sdDateTime }}</dd>
          </div>
        </dl>
        @if (a.hasPendingChanges) {
          <p class="pending"><sd-icon name="info" [size]="13" /> The working version has changes that are not published yet.</p>
        }
        @if (a.sourceTicketId) {
          <p class="source">
            <sd-icon name="ticket" [size]="13" />
            Created from <a [routerLink]="['/tickets', a.sourceTicketId]">ticket {{ a.sourceTicketNumber ?? '' }}</a>
          </p>
        }
      } @else {
        <div class="status-row">
          <sd-badge dot>New draft</sd-badge>
        </div>
        <p class="sd-small sd-muted">Save to create the first version.</p>
      }
    </section>

    <section class="panel" aria-labelledby="kb-props-class">
      <h3 id="kb-props-class" class="panel-title">Classification</h3>
      <sd-form-field label="Category" [error]="errors().categoryId">
        <sd-combobox
          placeholder="Uncategorised"
          [options]="store.categoryOptions()"
          [disabled]="disabled()"
          [value]="draft().categoryId"
          (valueChange)="patch({ categoryId: comboOne($event) })"
        />
      </sd-form-field>
      <sd-form-field label="Article type" [error]="errors().articleType">
        <select sdInput [disabled]="disabled()" [ngModel]="draft().articleType" (ngModelChange)="setType($event)">
          @for (t of types; track t) {
            <option [value]="t">{{ typeLabel[t] }}</option>
          }
        </select>
      </sd-form-field>
      <sd-form-field label="Tags" [error]="errors().tags" hint="Enter or comma adds a tag.">
        <sd-chips-input
          placeholder="Add tag"
          [suggestions]="store.options().tags"
          [lowercase]="true"
          [disabled]="disabled()"
          [value]="draft().tags"
          (valueChange)="patch({ tags: $event })"
        />
      </sd-form-field>
      <sd-form-field label="Product" [error]="errors().productId">
        <sd-combobox
          placeholder="No product"
          [options]="store.productOptions()"
          [disabled]="disabled()"
          [value]="draft().productId"
          (valueChange)="patch({ productId: comboOne($event) })"
        />
      </sd-form-field>
      <sd-form-field label="Related articles" [error]="errors().relatedArticleIds">
        <sd-combobox
          multiple
          placeholder="Search articles"
          [options]="relatedSeed()"
          [search]="searchArticles"
          [disabled]="disabled()"
          [value]="draft().relatedIds"
          (valueChange)="patch({ relatedIds: comboMany($event) })"
        />
      </sd-form-field>
    </section>

    <section class="panel" aria-labelledby="kb-props-audience">
      <h3 id="kb-props-audience" class="panel-title">Audience</h3>
      <sd-form-field label="Visibility" [hint]="visibilityHint()" [error]="errors().visibility">
        <select sdInput [disabled]="disabled()" [ngModel]="draft().visibility" (ngModelChange)="setVisibility($event)">
          @for (v of visibilities; track v) {
            <option [value]="v">{{ visibilityMeta[v].label }}</option>
          }
        </select>
      </sd-form-field>
      @if (draft().visibility === 'Restricted') {
        <desk-kb-access-rules [rules]="draft().rules" (rulesChange)="setRules($event)" [disabled]="disabled()" [error]="errors().accessRules" />
      }
      <sd-form-field label="Brands" hint="Empty = every brand portal." [error]="errors().brandIds">
        <sd-combobox
          multiple
          placeholder="All brands"
          [options]="store.brandOptions()"
          [disabled]="disabled()"
          [value]="draft().brandIds"
          (valueChange)="patch({ brandIds: comboMany($event) })"
        />
      </sd-form-field>
      <sd-form-field label="Primary language" [error]="errors().primaryLocale">
        <select sdInput [disabled]="disabled()" [ngModel]="draft().primaryLocale" (ngModelChange)="patch({ primaryLocale: $event })">
          <option value="">Workspace default</option>
          @for (l of localeChoices(); track l) {
            <option [value]="l">{{ localeName(l) }}</option>
          }
        </select>
      </sd-form-field>
    </section>

    <section class="panel" aria-labelledby="kb-props-owner">
      <h3 id="kb-props-owner" class="panel-title">Ownership & lifecycle</h3>
      <sd-form-field label="Owner" hint="Responsible for keeping it accurate." [error]="errors().ownerUserId">
        <sd-combobox
          placeholder="Unassigned"
          [options]="store.ownerOptions()"
          [disabled]="disabled()"
          [value]="draft().ownerUserId"
          (valueChange)="patch({ ownerUserId: comboOne($event) })"
        />
      </sd-form-field>
      <sd-form-field label="Review due" [error]="errors().reviewDueAt">
        <sd-date-input [disabled]="disabled()" [value]="draft().reviewDue" (valueChange)="patch({ reviewDue: $event })" />
      </sd-form-field>
      <sd-form-field label="Expires" hint="Unpublished automatically after this date." [error]="errors().expiresAt">
        <sd-date-input [disabled]="disabled()" [value]="draft().expires" (valueChange)="patch({ expires: $event })" />
      </sd-form-field>
      <sd-form-field label="URL slug" hint="Leave empty to generate it from the title." [error]="errors().slug">
        <input sdInput [disabled]="disabled()" maxlength="200" [ngModel]="draft().slug" (ngModelChange)="patch({ slug: $event })" />
      </sd-form-field>
    </section>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--sd-space-3);
      align-content: start;
    }
    .panel {
      display: grid;
      gap: var(--sd-space-3);
      padding: var(--sd-space-4);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .panel-title {
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
      color: var(--sd-text-3);
    }
    .status-row {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .facts {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
      margin: 0;
    }
    dt {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    dd {
      margin: 0;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .pending,
    .source {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: var(--sd-text-xs);
      color: var(--sd-text-2);
    }
  `,
})
export class ArticleProperties {
  readonly draft = model.required<ArticleDraft>();
  readonly article = input<ArticleDetailDto | null>(null);
  readonly errors = input<ArticleErrors>({});
  readonly disabled = input(false, { transform: booleanAttribute });

  protected readonly store = inject(KbStore);
  private readonly api = inject(KnowledgeApi);
  protected readonly types = ALL_ARTICLE_TYPES;
  protected readonly typeLabel = ARTICLE_TYPE_LABEL;
  protected readonly visibilities = ALL_VISIBILITIES;
  protected readonly visibilityMeta = VISIBILITY_META;
  protected readonly comboOne = comboOne;
  protected readonly comboMany = comboMany;

  protected readonly visibilityHint = computed(() => VISIBILITY_META[this.draft().visibility]?.description ?? null);
  protected readonly localeChoices = computed(() => {
    const list = this.store.options().locales;
    const current = this.draft().primaryLocale;
    return current && !list.includes(current) ? [current, ...list] : list;
  });
  /** Known labels for the related-articles picker (from the loaded article). */
  protected readonly relatedSeed = computed<SdOption[]>(() =>
    (this.article()?.relatedArticles ?? []).map((r) => ({ value: r.id, label: r.title, icon: 'file-text', description: r.isLive ? 'Live' : r.status })),
  );

  protected readonly searchArticles: SdOptionSource = (q: string) =>
    this.api.articles({ q: q || null, pageSize: 15, sort: q ? null : 'updated' }).pipe(
      map((page) =>
        page.items
          .filter((i) => i.id !== this.article()?.id)
          .map((i) => ({ value: i.id, label: i.title, icon: 'file-text', description: i.isLive ? 'Live' : i.status })),
      ),
    );

  protected localeName(tag: string): string {
    return localeLabel(tag);
  }

  protected patch(changes: Partial<ArticleDraft>): void {
    this.draft.update((d) => ({ ...d, ...changes }));
  }

  protected setType(type: ArticleType): void {
    this.patch({ articleType: type });
  }

  protected setVisibility(visibility: KbVisibility): void {
    this.patch({ visibility });
  }

  protected setRules(rules: RuleDraft[]): void {
    this.patch({ rules });
  }
}
