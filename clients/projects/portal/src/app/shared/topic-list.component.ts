import { ChangeDetectionStrategy, Component, booleanAttribute, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TopicListItemDto } from '@sd/core';
import { SdBadge, SdIcon, SdRelativeTimePipe } from '@sd/ui';
import { BrandService } from '../brand.service';

/** Community topics with votes, answer state and activity: `<portal-topic-list [topics]="items" />` */
@Component({
  selector: 'portal-topic-list',
  imports: [RouterLink, SdIcon, SdBadge, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="topics" [class.is-compact]="compact()">
      @for (t of topics(); track t.id) {
        <li class="topic" [class.is-pinned]="t.isPinned">
          <div class="score">
            <span class="num">{{ t.voteScore }}</span> <span class="lbl">votes</span>
          </div>
          <div class="score answers" [class.is-answered]="t.isAnswered">
            <span class="num">
              @if (t.isAnswered) {
                <sd-icon name="check" [size]="12" />
              }
              {{ t.postCount }}</span
            > <span class="lbl">{{ t.postCount === 1 ? 'reply' : 'replies' }}</span>
          </div>
          <div class="main">
            <div class="title-row">
              @if (t.isPinned) {
                <sd-icon name="pin" [size]="14" label="Pinned" class="pin" />
              }
              <a class="title" [routerLink]="[brand.base(), 'community', t.id]">{{ t.title }}</a>
            </div>
            @if (!compact() && t.excerpt) {
              <p class="excerpt">{{ t.excerpt }}</p>
            }
            <div class="meta">
              <sd-badge size="sm" [tone]="t.isQuestion ? 'primary' : 'neutral'" [icon]="t.isQuestion ? 'help' : 'messages'">{{
                t.isQuestion ? 'Question' : 'Discussion'
              }}</sd-badge>
              @if (t.isAnswered) {
                <sd-badge size="sm" tone="success" icon="circle-check">Answered</sd-badge>
              }
              @if (t.status === 'Closed') {
                <sd-badge size="sm" icon="lock">Closed</sd-badge>
              }
              @for (tag of t.tags.slice(0, 3); track tag) {
                <span class="tag">#{{ tag }}</span>
              }
              <span class="by">
                {{ t.authorName }}
                @if (t.authorIsAgent) {
                  <sd-icon name="badge-check" [size]="13" label="Support team" class="agent" />
                }
                · active {{ t.lastActivityAt | sdRelativeTime }}
              </span>
            </div>
          </div>
        </li>
      }
    </ul>
  `,
  styles: `
    .topics {
      display: grid;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .topic {
      display: flex;
      align-items: flex-start;
      gap: 12px;
      padding: 14px 4px;
      border-bottom: 1px solid var(--sd-divider);
    }
    .topic:last-child {
      border-bottom: 0;
    }
    .is-compact .topic {
      padding: 10px 2px;
    }
    .score {
      display: grid;
      justify-items: center;
      flex: none;
      width: 52px;
      padding: 5px 0;
      border-radius: var(--sd-radius-md);
      color: var(--sd-text-2);
    }
    .score .num {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      font-weight: var(--sd-weight-semibold);
      font-size: var(--sd-text-md);
      color: var(--sd-text);
    }
    .score .lbl {
      font-size: 11px;
      color: var(--sd-text-3);
    }
    .answers {
      border: 1px solid var(--sd-border);
    }
    .answers.is-answered {
      border-color: var(--sd-success-border);
      background: var(--sd-success-bg);
    }
    .answers.is-answered .num,
    .answers.is-answered .lbl {
      color: var(--sd-success);
    }
    .main {
      flex: 1;
      min-width: 0;
      display: grid;
      gap: 5px;
    }
    .title-row {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .pin {
      color: var(--sd-accent);
    }
    .title {
      color: var(--sd-text);
      font-weight: var(--sd-weight-semibold);
      overflow-wrap: anywhere;
    }
    .title:hover {
      color: var(--sd-primary);
    }
    .excerpt {
      margin: 0;
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      display: -webkit-box;
      -webkit-line-clamp: 2;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 8px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .tag {
      color: var(--sd-text-2);
    }
    .by {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .agent {
      color: var(--sd-primary);
    }
    @media (max-width: 560px) {
      .score {
        width: 44px;
      }
      .score:not(.answers) {
        display: none;
      }
    }
  `,
})
export class TopicListComponent {
  protected readonly brand = inject(BrandService);
  readonly topics = input.required<TopicListItemDto[]>();
  readonly compact = input(false, { transform: booleanAttribute });
}
