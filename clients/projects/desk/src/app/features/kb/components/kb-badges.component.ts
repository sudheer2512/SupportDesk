import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ArticleStatus, KbVisibility, TopicStatus, TranslationStatus } from '@sd/core';
import { SdBadge, SdIcon } from '@sd/ui';
import {
  ARTICLE_STATUS_LABEL,
  ARTICLE_STATUS_TONE,
  TOPIC_STATUS_TONE,
  TRANSLATION_STATUS_LABEL,
  TRANSLATION_STATUS_TONE,
  VISIBILITY_META,
} from '../kb-common';

/** `<desk-kb-status [status]="a.status" />` — article / flow workflow status pill. */
@Component({
  selector: 'desk-kb-status',
  imports: [SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-badge [tone]="tone()" [variant]="status() === 'Archived' ? 'outline' : 'soft'" [size]="size()" dot>{{ label() }}</sd-badge>`,
})
export class KbStatusBadge {
  readonly status = input.required<ArticleStatus>();
  readonly size = input<'sm' | 'md'>('md');
  protected readonly label = computed(() => ARTICLE_STATUS_LABEL[this.status()] ?? this.status());
  protected readonly tone = computed(() => ARTICLE_STATUS_TONE[this.status()] ?? 'neutral');
}

/** `<desk-kb-translation-status [status]="t.status" />` */
@Component({
  selector: 'desk-kb-translation-status',
  imports: [SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-badge [tone]="tone()" size="sm" dot>{{ label() }}</sd-badge>`,
})
export class KbTranslationStatusBadge {
  readonly status = input.required<TranslationStatus>();
  protected readonly label = computed(() => TRANSLATION_STATUS_LABEL[this.status()] ?? this.status());
  protected readonly tone = computed(() => TRANSLATION_STATUS_TONE[this.status()] ?? 'neutral');
}

/** `<desk-kb-topic-status [status]="t.status" />` */
@Component({
  selector: 'desk-kb-topic-status',
  imports: [SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-badge [tone]="tone()" size="sm" dot>{{ status() }}</sd-badge>`,
})
export class KbTopicStatusBadge {
  readonly status = input.required<TopicStatus>();
  protected readonly tone = computed(() => TOPIC_STATUS_TONE[this.status()] ?? 'neutral');
}

/** `<desk-kb-visibility [visibility]="a.visibility" />` — visibility with its icon. */
@Component({
  selector: 'desk-kb-visibility',
  imports: [SdBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<sd-badge variant="outline" size="sm" [icon]="meta().icon" [attr.title]="meta().description">{{ meta().label }}</sd-badge>`,
})
export class KbVisibilityBadge {
  readonly visibility = input.required<KbVisibility>();
  protected readonly meta = computed(() => VISIBILITY_META[this.visibility()] ?? VISIBILITY_META.Internal);
}

/** `<desk-kb-stars [rating]="4" />` — read-only 1–5 star rating with an accessible label. */
@Component({
  selector: 'desk-kb-stars',
  imports: [SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="stars" role="img" [attr.aria-label]="label()">
      @for (s of slots; track s) {
        <sd-icon name="star" [size]="size()" [class.on]="s <= rounded()" />
      }
    </span>
  `,
  styles: `
    .stars {
      display: inline-flex;
      gap: 1px;
      color: var(--sd-border-strong);
      vertical-align: middle;
    }
    .on {
      color: var(--sd-warning);
    }
    .on ::ng-deep svg {
      fill: currentColor;
    }
  `,
})
export class KbStars {
  readonly rating = input<number | null>(null);
  readonly size = input(13);
  protected readonly slots = [1, 2, 3, 4, 5];
  protected readonly rounded = computed(() => Math.round(this.rating() ?? 0));
  protected readonly label = computed(() => {
    const r = this.rating();
    return r === null ? 'Not rated' : `Rated ${Number.isInteger(r) ? r : r.toFixed(1)} out of 5`;
  });
}
