import { ChangeDetectionStrategy, Component, booleanAttribute, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { ArticleDetailDto, ArticleVersionDto, KnowledgeApi, ToastBridge } from '@sd/core';
import { SdBadge, SdButton, SdCheckbox, SdDateTimePipe, SdDialogService, SdEmptyState, SdIcon, SdRelativeTimePipe, SdSkeleton } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { KbStatusBadge } from '../components/kb-badges.component';
import { promptText } from '../components/kb-prompt.dialog';
import { KB_STYLES, errorMessage, remote, toastedGlobally } from '../kb-common';
import { DiffDialogData, VersionDialogData, VersionDiffDialog, VersionViewDialog } from './version-dialogs';

/**
 * Version history of an article: view any version, compare two (or one against the current version),
 * restore an older version as a new working version.
 */
@Component({
  selector: 'desk-kb-article-versions',
  imports: [SdButton, SdBadge, SdIcon, SdCheckbox, SdSkeleton, SdEmptyState, SdDateTimePipe, SdRelativeTimePipe, KbStatusBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="bar">
      <p class="sd-small sd-secondary">Tick two versions to compare them, or compare any version with the current one.</p>
      <span class="sd-spacer"></span>
      <button sdButton size="sm" icon="git-compare" type="button" [disabled]="compare().length !== 2" (click)="compareSelected()">
        {{ compareLabel() }}
      </button>
    </div>

    @if (versions.loading() && !versions.value()) {
      <div class="loading" aria-busy="true" aria-label="Loading versions">
        @for (n of [1, 2, 3, 4]; track n) {
          <sd-skeleton height="38px" radius="var(--sd-radius-sm)" />
        }
      </div>
    } @else if (versions.error(); as err) {
      <div class="kb-error" role="alert">
        <sd-icon name="circle-alert" [size]="16" />
        <p>Could not load the version history. {{ err }}</p>
        <button sdButton size="sm" type="button" icon="refresh" (click)="versions.reload()">Retry</button>
      </div>
    } @else if (list().length === 0) {
      <sd-empty-state icon="history" title="No versions yet" message="Every save creates a version you can compare or restore." compact />
    } @else {
      <div class="kb-table-wrap">
        <table class="kb-table">
          <caption class="sd-visually-hidden">Article versions, newest first</caption>
          <thead>
            <tr>
              <th scope="col"><span class="sd-visually-hidden">Compare</span></th>
              <th scope="col">Version</th>
              <th scope="col">Status</th>
              <th scope="col">Author</th>
              <th scope="col">Saved</th>
              <th scope="col">Change note</th>
              <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (v of list(); track v.id) {
              <tr [class.is-current]="v.isCurrent">
                <td class="check">
                  <sd-checkbox
                    [value]="compare().includes(v.versionNumber)"
                    [ariaLabel]="'Compare version ' + v.versionNumber"
                    (valueChange)="toggleCompare(v.versionNumber, $event)"
                  />
                </td>
                <td class="nowrap">
                  <strong>v{{ v.versionNumber }}</strong>
                  @if (v.isCurrent) {
                    <sd-badge tone="primary" size="sm">Current</sd-badge>
                  }
                  @if (v.isPublished) {
                    <sd-badge tone="success" size="sm" icon="globe">Published</sd-badge>
                  }
                </td>
                <td><desk-kb-status [status]="v.status" size="sm" /></td>
                <td class="nowrap">{{ v.authorName ?? '—' }}</td>
                <td class="nowrap" [attr.title]="v.createdAt | sdDateTime">{{ v.createdAt | sdRelativeTime }}</td>
                <td class="note">
                  @if (v.changeNote) {
                    {{ v.changeNote }}
                  } @else {
                    <span class="sd-muted">—</span>
                  }
                  @if (v.reviewNote) {
                    <div class="review"><sd-icon name="messages" [size]="12" /> {{ v.reviewerName ?? 'Reviewer' }}: {{ v.reviewNote }}</div>
                  }
                </td>
                <td class="actions">
                  <button sdButton size="sm" variant="ghost" icon="eye" type="button" (click)="view(v)" [attr.aria-label]="'View version ' + v.versionNumber">View</button>
                  @if (!v.isCurrent && current(); as cur) {
                    <button sdButton size="sm" variant="ghost" icon="git-compare" type="button" (click)="openDiff(v.versionNumber, cur.versionNumber)" [attr.aria-label]="'Compare version ' + v.versionNumber + ' with the current version'">
                      Diff
                    </button>
                  }
                  @if (canRestore() && !v.isCurrent) {
                    <button
                      sdButton
                      size="sm"
                      variant="ghost"
                      icon="history"
                      type="button"
                      [loading]="restoring() === v.versionNumber"
                      (click)="restore(v)"
                      [attr.aria-label]="'Restore version ' + v.versionNumber"
                    >
                      Restore
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: [
    KB_STYLES,
    `
      :host {
        display: grid;
        gap: var(--sd-space-3);
      }
      .bar {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sd-space-2);
      }
      .loading {
        display: grid;
        gap: 6px;
      }
      .check {
        width: 36px;
      }
      .nowrap {
        white-space: nowrap;
      }
      .nowrap sd-badge {
        margin-left: 4px;
      }
      .note {
        min-width: 180px;
      }
      .review {
        display: flex;
        align-items: flex-start;
        gap: 4px;
        margin-top: 4px;
        color: var(--sd-text-3);
        font-size: var(--sd-text-xs);
      }
      .actions {
        white-space: nowrap;
        text-align: right;
      }
      tr.is-current td {
        background: color-mix(in oklab, var(--sd-primary-soft) 45%, transparent);
      }
    `,
  ],
})
export class ArticleVersions {
  readonly articleId = input.required<string>();
  /** Changes whenever the article is saved/transitioned so the list refreshes. */
  readonly revision = input<string>('');
  readonly canRestore = input(false, { transform: booleanAttribute });
  /** The editor has unsaved edits (restoring discards them). */
  readonly dirty = input(false, { transform: booleanAttribute });
  readonly restored = output<ArticleDetailDto>();

  private readonly api = inject(KnowledgeApi);
  private readonly dialogs = inject(SdDialogService);
  private readonly toasts = inject(ToastBridge);

  protected readonly versions = remote<ArticleVersionDto[]>();
  protected readonly list = computed(() => [...(this.versions.value() ?? [])].sort((a, b) => b.versionNumber - a.versionNumber));
  protected readonly current = computed(() => this.list().find((v) => v.isCurrent) ?? this.list()[0] ?? null);
  protected readonly compare = signal<number[]>([]);
  protected readonly restoring = signal<number | null>(null);
  protected readonly compareLabel = computed(() => {
    const [a, b] = [...this.compare()].sort((x, y) => x - y);
    return a !== undefined && b !== undefined ? `Compare v${a} → v${b}` : 'Compare selected';
  });

  constructor() {
    effect(() => {
      const id = this.articleId();
      this.revision();
      untracked(() => this.versions.load(() => this.api.versions(id), { keepValue: true }));
    });
  }

  protected toggleCompare(n: number, on: boolean): void {
    this.compare.update((list) => {
      const without = list.filter((x) => x !== n);
      if (!on) return without;
      return [...without, n].slice(-2);
    });
  }

  protected compareSelected(): void {
    const [a, b] = [...this.compare()].sort((x, y) => x - y);
    if (a !== undefined && b !== undefined) this.openDiff(a, b);
  }

  protected openDiff(from: number, to: number): void {
    const [a, b] = from <= to ? [from, to] : [to, from];
    this.dialogs.open<void, DiffDialogData, VersionDiffDialog>(VersionDiffDialog, { data: { articleId: this.articleId(), from: a, to: b }, size: 'xl' });
  }

  protected view(v: ArticleVersionDto): void {
    this.dialogs.open<void, VersionDialogData, VersionViewDialog>(VersionViewDialog, {
      data: { articleId: this.articleId(), versionNumber: v.versionNumber },
      size: 'lg',
    });
  }

  protected async restore(v: ArticleVersionDto): Promise<void> {
    const note = await promptText(this.dialogs, {
      title: `Restore version ${v.versionNumber}?`,
      message: this.dirty()
        ? 'Its content becomes a new working version. Your unsaved edits in the editor will be discarded.'
        : 'Its content becomes a new working version; nothing is deleted from the history.',
      label: 'Change note',
      initial: `Restored version ${v.versionNumber}`,
      confirmLabel: 'Restore',
    });
    if (note === null) return;
    this.restoring.set(v.versionNumber);
    try {
      const article = await firstValueFrom(this.api.restore(this.articleId(), v.versionNumber, note || null));
      this.toasts.success(`Version ${v.versionNumber} restored`, `Saved as version ${article.currentVersionNumber}.`);
      this.compare.set([]);
      this.restored.emit(article);
    } catch (err) {
      if (!toastedGlobally(err)) this.toasts.error('Could not restore the version', errorMessage(err));
    } finally {
      this.restoring.set(null);
    }
  }
}
