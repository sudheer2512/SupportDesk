import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { AnalyticsApi, AnalyticsExportDto, ApiError } from '@sd/core';
import { SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdFileSizePipe, SdSkeleton, ToastService } from '@sd/ui';
import { AnalyticsStore } from './analytics.store';
import { saveBlob } from './analytics-format';

/** /analytics/exports — the caller's recent report exports (rendered server-side), with download. */
@Component({
  selector: 'desk-analytics-exports',
  imports: [SdBadge, SdButton, SdDateTimePipe, SdEmptyState, SdFileSizePipe, SdSkeleton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (store.catalog() && !store.catalog()!.canExport) {
      <sd-empty-state icon="lock" title="Exports are not available" message="Ask an administrator for the “analytics.export” permission." />
    } @else {
      <div class="toolbar">
        <p class="sd-small sd-muted intro">Use the Export menu on any report. Files stay available here for download.</p>
        <button sdButton size="sm" icon="refresh" type="button" (click)="load()">Refresh</button>
      </div>
      @if (loading() && items().length === 0) {
        <div aria-busy="true"><sd-skeleton [lines]="6" /></div>
      } @else if (error()) {
        <sd-empty-state icon="circle-alert" title="Exports could not be loaded" [message]="error()">
          <button sdButton type="button" (click)="load()">Retry</button>
        </sd-empty-state>
      } @else if (items().length === 0) {
        <sd-empty-state icon="download" title="No exports yet" message="Open a report and choose Export → Excel, CSV or PDF." />
      } @else {
        <div class="wrap">
          <table>
            <caption class="sd-visually-hidden">Report exports</caption>
            <thead>
              <tr>
                <th scope="col">Report</th>
                <th scope="col">Format</th>
                <th scope="col">Status</th>
                <th scope="col">Requested</th>
                <th scope="col" class="num">Rows</th>
                <th scope="col" class="num">Size</th>
                <th scope="col"><span class="sd-visually-hidden">Download</span></th>
              </tr>
            </thead>
            <tbody>
              @for (e of items(); track e.id) {
                <tr>
                  <th scope="row">{{ e.title }}</th>
                  <td>{{ e.format === 'Xlsx' ? 'Excel' : e.format.toUpperCase() }}</td>
                  <td>
                    <sd-badge size="sm" [tone]="e.status === 'Succeeded' ? 'success' : e.status === 'Failed' ? 'danger' : 'info'">{{ e.status }}</sd-badge>
                    @if (e.error) {
                      <div class="sd-xs err">{{ e.error }}</div>
                    }
                  </td>
                  <td>{{ e.createdAt | sdDateTime }}</td>
                  <td class="num">{{ e.status === 'Succeeded' ? e.rowCount : '—' }}</td>
                  <td class="num">{{ e.status === 'Succeeded' ? (e.sizeBytes | sdFileSize) : '—' }}</td>
                  <td>
                    @if (e.status === 'Succeeded') {
                      <button sdButton size="sm" icon="download" type="button" [loading]="downloading() === e.id" (click)="download(e)" [attr.aria-label]="'Download ' + e.fileName">Download</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--sd-space-3);
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: var(--sd-space-2);
    }
    .intro {
      margin: 0;
    }
    .wrap {
      overflow-x: auto;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--sd-text-sm);
    }
    th,
    td {
      padding: 8px 12px;
      border-bottom: 1px solid var(--sd-divider);
      text-align: left;
      white-space: nowrap;
    }
    thead th {
      color: var(--sd-text-2);
      font-weight: var(--sd-weight-medium);
    }
    tbody th {
      font-weight: var(--sd-weight-medium);
    }
    .num {
      text-align: right;
      font-variant-numeric: tabular-nums;
    }
    .err {
      color: var(--sd-danger);
      white-space: normal;
      max-width: 260px;
    }
  `,
})
export class AnalyticsExportsPage {
  private readonly api = inject(AnalyticsApi);
  private readonly toast = inject(ToastService);
  protected readonly store = inject(AnalyticsStore);

  protected readonly items = signal<AnalyticsExportDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly downloading = signal<string | null>(null);

  constructor() {
    this.store.ensure();
    this.load();
    // Pending exports finish in the background: refresh while any is queued or running.
    const timer = setInterval(() => {
      if (this.items().some((e) => e.status === 'Queued' || e.status === 'Running')) this.load();
    }, 4000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.exports().subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        const err = ApiError.from(e);
        this.error.set(err.status === 403 ? 'You do not have permission to export reports.' : err.userMessage);
        this.loading.set(false);
      },
    });
  }

  protected download(e: AnalyticsExportDto): void {
    this.downloading.set(e.id);
    this.api.downloadExport(e.id).subscribe({
      next: (blob) => {
        saveBlob(blob, e.fileName ?? 'report');
        this.downloading.set(null);
      },
      error: (err: unknown) => {
        this.downloading.set(null);
        this.toast.error('Download failed', ApiError.from(err).userMessage);
      },
    });
  }
}
