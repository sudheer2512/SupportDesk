import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ArticleDiffDto, ArticleVersionContentDto, FieldDiffDto, KnowledgeApi, MeStore, formatDateTime } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdDialog, SdEmptyState, SdIcon, SdSkeleton } from '@sd/ui';
import { KbStatusBadge } from '../components/kb-badges.component';
import { remote } from '../kb-common';

export interface VersionDialogData {
  articleId: string;
  versionNumber: number;
}

/** Read-only rendering of one article version. */
@Component({
  selector: 'desk-kb-version-view-dialog',
  imports: [SdDialog, SdButton, SdBadge, SdSkeleton, SdEmptyState, SdDateTimePipe, KbStatusBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Version ' + data.versionNumber" [description]="description()">
      @if (content.loading() && !content.value()) {
        <div class="loading" aria-busy="true" aria-label="Loading version">
          <sd-skeleton width="60%" height="22px" />
          <sd-skeleton [lines]="6" />
        </div>
      } @else if (content.error(); as err) {
        <sd-empty-state icon="circle-alert" title="Could not load this version" [message]="err" compact>
          <button sdButton type="button" icon="refresh" (click)="content.reload()">Retry</button>
        </sd-empty-state>
      } @else if (content.value(); as c) {
        <div class="meta">
          <desk-kb-status [status]="c.version.status" size="sm" />
          @if (c.version.isCurrent) {
            <sd-badge tone="primary" size="sm">Current</sd-badge>
          }
          @if (c.version.isPublished) {
            <sd-badge tone="success" size="sm" icon="globe">Published</sd-badge>
          }
          <sd-badge variant="outline" size="sm">{{ c.version.locale }}</sd-badge>
        </div>
        @if (c.version.changeNote) {
          <p class="note"><strong>Change note:</strong> {{ c.version.changeNote }}</p>
        }
        @if (c.version.reviewNote) {
          <p class="note">
            <strong>Review by {{ c.version.reviewerName ?? 'reviewer' }}</strong>
            @if (c.version.reviewedAt) {
              ({{ c.version.reviewedAt | sdDateTime }})
            }
            : {{ c.version.reviewNote }}
          </p>
        }
        <article class="doc">
          <h3 class="doc-title">{{ c.version.title }}</h3>
          @if (c.version.summary) {
            <p class="summary">{{ c.version.summary }}</p>
          }
          <div class="sd-prose" [innerHTML]="c.bodyHtml"></div>
          @if (c.keywords) {
            <p class="keywords"><strong>Keywords:</strong> {{ c.keywords }}</p>
          }
        </article>
      }
      <div sdDialogFooter>
        <button sdButton variant="primary" type="button" (click)="close()">Close</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .loading {
      display: grid;
      gap: 12px;
    }
    .meta {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: var(--sd-space-3);
    }
    .note {
      margin-bottom: var(--sd-space-2);
      padding: 8px 10px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-2);
      font-size: var(--sd-text-sm);
    }
    .doc {
      padding: var(--sd-space-4);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    .doc-title {
      margin-bottom: var(--sd-space-2);
      font-size: var(--sd-text-xl);
    }
    .summary {
      margin-bottom: var(--sd-space-3);
      color: var(--sd-text-2);
    }
    .keywords {
      margin-top: var(--sd-space-3);
      color: var(--sd-text-3);
      font-size: var(--sd-text-sm);
    }
  `,
})
export class VersionViewDialog {
  protected readonly data = inject<VersionDialogData>(DIALOG_DATA);
  private readonly api = inject(KnowledgeApi);
  private readonly ref = inject(DialogRef);
  protected readonly content = remote<ArticleVersionContentDto>();
  private readonly me = inject(MeStore);
  protected readonly description = computed(() => {
    const v = this.content.value()?.version;
    if (!v) return null;
    const when = formatDateTime(v.createdAt, { timeZone: this.me.timeZone(), locale: this.me.locale() });
    return `${v.authorName ?? 'Unknown author'} · ${when}`;
  });

  constructor() {
    this.content.load(() => this.api.version(this.data.articleId, this.data.versionNumber));
  }

  protected close(): void {
    this.ref.close();
  }
}

export interface DiffDialogData {
  articleId: string;
  from: number;
  to: number;
}

interface FieldRow {
  label: string;
  diff: FieldDiffDto;
}

/** Side-by-side field changes and a line diff of the body between two versions. */
@Component({
  selector: 'desk-kb-version-diff-dialog',
  imports: [SdDialog, SdButton, SdBadge, SdIcon, SdSkeleton, SdEmptyState],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dialog [title]="'Compare v' + data.from + ' → v' + data.to" description="Removed text is red, added text is green; unchanged lines are muted.">
      @if (diff.loading() && !diff.value()) {
        <div class="loading" aria-busy="true" aria-label="Loading comparison"><sd-skeleton [lines]="8" /></div>
      } @else if (diff.error(); as err) {
        <sd-empty-state icon="circle-alert" title="Could not compare these versions" [message]="err" compact>
          <button sdButton type="button" icon="refresh" (click)="diff.reload()">Retry</button>
        </sd-empty-state>
      } @else if (diff.value(); as d) {
        <div class="summary" role="status">
          <sd-badge tone="success" size="sm" icon="plus">{{ d.added }} added</sd-badge>
          <sd-badge tone="danger" size="sm" icon="minus">{{ d.removed }} removed</sd-badge>
        </div>
        @if (fields().length) {
          <dl class="fields">
            @for (f of fields(); track f.label) {
              <div class="field">
                <dt>{{ f.label }}</dt>
                <dd>
                  <del class="from"><span class="sd-visually-hidden">Before: </span>{{ f.diff.from || '(empty)' }}</del>
                  <sd-icon name="arrow-right" [size]="13" />
                  <ins class="to"><span class="sd-visually-hidden">After: </span>{{ f.diff.to || '(empty)' }}</ins>
                </dd>
              </div>
            }
          </dl>
        }
        <h3 class="body-title">Body</h3>
        @if (d.body.length) {
          <ol class="lines" aria-label="Body changes">
            @for (line of d.body; track $index) {
              <li [class]="'line line--' + line.op">
                <span class="mark" aria-hidden="true">{{ line.op === 'insert' ? '+' : line.op === 'delete' ? '−' : ' ' }}</span>
                @if (line.op === 'insert') {
                  <span class="sd-visually-hidden">Added: </span>
                } @else if (line.op === 'delete') {
                  <span class="sd-visually-hidden">Removed: </span>
                }
                <span class="text">{{ line.text || ' ' }}</span>
              </li>
            }
          </ol>
        } @else {
          <p class="sd-muted sd-small">The body is identical.</p>
        }
      }
      <div sdDialogFooter>
        <button sdButton variant="primary" type="button" (click)="close()">Close</button>
      </div>
    </sd-dialog>
  `,
  styles: `
    .summary {
      display: flex;
      gap: 6px;
      margin-bottom: var(--sd-space-3);
    }
    .fields {
      display: grid;
      gap: 8px;
      margin: 0 0 var(--sd-space-4);
    }
    .field {
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
    }
    dt {
      margin-bottom: 4px;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      font-weight: var(--sd-weight-semibold);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    dd {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
      margin: 0;
    }
    .from {
      padding: 1px 4px;
      border-radius: var(--sd-radius-xs);
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .to {
      padding: 1px 4px;
      border-radius: var(--sd-radius-xs);
      background: var(--sd-success-bg);
      color: var(--sd-success);
      text-decoration: none;
    }
    .body-title {
      margin-bottom: var(--sd-space-2);
      font-size: var(--sd-text-md);
    }
    .lines {
      margin: 0;
      padding: 0;
      max-height: 52vh;
      overflow: auto;
      list-style: none;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-sm);
      font-family: var(--sd-font-mono);
      font-size: var(--sd-text-xs);
    }
    .line {
      display: flex;
      gap: 8px;
      padding: 2px 8px;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .line--equal {
      color: var(--sd-text-3);
    }
    .line--insert {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .line--delete {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
      text-decoration: line-through;
      text-decoration-thickness: 1px;
    }
    .mark {
      flex: none;
      width: 1ch;
      font-weight: var(--sd-weight-bold);
      text-decoration: none;
    }
    .text {
      min-width: 0;
    }
  `,
})
export class VersionDiffDialog {
  protected readonly data = inject<DiffDialogData>(DIALOG_DATA);
  private readonly api = inject(KnowledgeApi);
  private readonly ref = inject(DialogRef);
  protected readonly diff = remote<ArticleDiffDto>();
  protected readonly fields = computed<FieldRow[]>(() => {
    const d = this.diff.value();
    if (!d) return [];
    const rows: FieldRow[] = [];
    if (d.title) rows.push({ label: 'Title', diff: d.title });
    if (d.summary) rows.push({ label: 'Summary', diff: d.summary });
    if (d.keywords) rows.push({ label: 'Keywords', diff: d.keywords });
    return rows;
  });

  constructor() {
    this.diff.load(() => this.api.diff(this.data.articleId, this.data.from, this.data.to));
  }

  protected close(): void {
    this.ref.close();
  }
}
