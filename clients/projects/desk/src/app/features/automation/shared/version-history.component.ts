import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { RuleVersionDto, RuleVersionSummaryDto } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe } from '@sd/ui';
import { Observable } from 'rxjs';
import { DiffLine, diffLines, prettyJson, stripNulls } from './automation-ui';

/**
 * Immutable version history of a rule or blueprint: state badges, "open in editor", publish a draft, roll back
 * (republish an older definition as a new version) and a line diff against the editor's current definition.
 */
@Component({
  selector: 'desk-version-history',
  imports: [SdBadge, SdButton, SdDateTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="versions">
      @for (v of versions(); track v.id) {
        <li class="version" [class.selected]="v.id === selectedId()">
          <div class="head">
            <strong>v{{ v.versionNumber }}</strong>
            <sd-badge size="sm" [tone]="v.state === 'Published' ? 'success' : v.state === 'Draft' ? 'info' : 'neutral'">{{ v.state }}</sd-badge>
            @if (v.id === loadedId()) {
              <sd-badge size="sm" variant="outline" tone="primary">in editor</sd-badge>
            }
          </div>
          <div class="sd-muted small">
            {{ v.createdAt | sdDateTime }}{{ v.createdByName ? ' · ' + v.createdByName : '' }}
            @if (v.publishedAt) {
              · published {{ v.publishedAt | sdDateTime }}
            }
          </div>
          @if (v.changeNote) {
            <div class="note">{{ v.changeNote }}</div>
          }
          <div class="buttons">
            <button sdButton size="sm" variant="ghost" icon="pencil" (click)="open.emit(v.id)">Open</button>
            <button sdButton size="sm" variant="ghost" icon="columns" [pressed]="v.id === selectedId()" (click)="compare(v)">Diff</button>
            @if (v.state === 'Draft') {
              <button sdButton size="sm" variant="subtle" icon="rocket" (click)="publish.emit(v.id)">Publish</button>
            }
            @if (v.state === 'Superseded' && canRollback()) {
              <button sdButton size="sm" variant="subtle" icon="history" (click)="rollback.emit(v.id)">Roll back</button>
            }
          </div>
          @if (v.id === selectedId()) {
            @if (diff(); as lines) {
              <p class="sd-muted small legend">Changes from v{{ v.versionNumber }} to the editor ({{ added() }} added, {{ removed() }} removed)</p>
              <pre class="diff" tabindex="0" aria-label="Definition diff">@for (l of lines; track $index) {<span [class]="'line ' + l.kind">{{ l.kind === 'add' ? '+ ' : l.kind === 'remove' ? '- ' : '  ' }}{{ l.text }}
</span>}</pre>
            } @else {
              <p class="sd-muted small">Loading…</p>
            }
          }
        </li>
      }
    </ol>
  `,
  styles: `
    .versions {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .version {
      padding: 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .version.selected {
      border-color: var(--sd-primary-border);
    }
    .head {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .small {
      font-size: var(--sd-text-xs);
    }
    .note {
      margin-top: 2px;
      font-size: var(--sd-text-sm);
    }
    .buttons {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin-top: 6px;
    }
    .legend {
      margin: 8px 0 4px;
    }
    .diff {
      max-height: 320px;
      margin: 0;
      padding: 6px;
      overflow: auto;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-sunken);
      font-family: var(--sd-font-mono);
      font-size: 11px;
      line-height: 1.45;
    }
    .line {
      display: block;
      white-space: pre;
    }
    .line.add {
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .line.remove {
      background: var(--sd-danger-bg);
      color: var(--sd-danger);
    }
    .line.same {
      color: var(--sd-text-3);
    }
  `,
})
export class VersionHistory {
  readonly versions = input.required<RuleVersionSummaryDto[]>();
  /** Version currently loaded into the editor. */
  readonly loadedId = input<string | null>(null);
  readonly current = input<unknown>(null);
  readonly loader = input.required<(versionId: string) => Observable<RuleVersionDto>>();
  readonly canRollback = input(true);
  readonly open = output<string>();
  readonly publish = output<string>();
  readonly rollback = output<string>();

  protected readonly selectedId = signal<string | null>(null);
  private readonly selectedDefinition = signal<unknown>(undefined);

  protected readonly diff = computed<DiffLine[] | null>(() => {
    const def = this.selectedDefinition();
    if (def === undefined) return null;
    return diffLines(prettyJson(stripNulls(def)), prettyJson(stripNulls(this.current())));
  });
  protected readonly added = computed(() => (this.diff() ?? []).filter((l) => l.kind === 'add').length);
  protected readonly removed = computed(() => (this.diff() ?? []).filter((l) => l.kind === 'remove').length);

  protected compare(v: RuleVersionSummaryDto): void {
    if (this.selectedId() === v.id) {
      this.selectedId.set(null);
      return;
    }
    this.selectedId.set(v.id);
    this.selectedDefinition.set(undefined);
    this.loader()(v.id).subscribe((full) => {
      if (this.selectedId() === v.id) this.selectedDefinition.set(full.definition);
    });
  }
}
