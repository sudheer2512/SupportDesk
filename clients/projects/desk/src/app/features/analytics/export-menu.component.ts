import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, signal } from '@angular/core';
import { AnalyticsApi, AnalyticsExportDto, AnalyticsReportFormat, ApiError } from '@sd/core';
import { SdDropdownMenu, SdMenuItem, ToastService } from '@sd/ui';
import { Subscription, TimeoutError, filter, switchMap, take, timeout, timer } from 'rxjs';
import { saveBlob } from './analytics-format';

/**
 * Export menu (CSV / Excel / PDF). Exports are rendered server-side by a durable job: the menu queues one, polls
 * until it is ready and downloads it; finished exports are also listed under Analytics › Exports.
 */
@Component({
  selector: 'desk-analytics-export-menu',
  imports: [SdDropdownMenu],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-dropdown-menu
      size="sm"
      icon="download"
      [label]="busy() ? 'Exporting…' : 'Export'"
      ariaLabel="Export report"
      align="end"
      [disabled]="busy()"
      [items]="items"
      (select)="run($event)"
    />
  `,
})
export class AnalyticsExportMenuComponent {
  private readonly api = inject(AnalyticsApi);
  private readonly toast = inject(ToastService);

  readonly reportKey = input.required<string>();
  /** Report parameters (period, compare, filters) as stored keys. */
  readonly parameters = input<Record<string, string>>({});

  protected readonly busy = signal(false);
  private sub: Subscription | null = null;

  protected readonly items: SdMenuItem[] = [
    { id: 'Xlsx', label: 'Excel workbook (.xlsx)', icon: 'file-spreadsheet' },
    { id: 'Csv', label: 'CSV (.csv)', icon: 'file-text' },
    { id: 'Pdf', label: 'PDF document (.pdf)', icon: 'file-text' },
  ];

  constructor() {
    inject(DestroyRef).onDestroy(() => this.sub?.unsubscribe());
  }

  protected run(item: SdMenuItem): void {
    const format = item.id as AnalyticsReportFormat;
    this.busy.set(true);
    this.sub?.unsubscribe();
    this.sub = this.api
      .createExport({ reportKey: this.reportKey(), format, parameters: this.parameters() })
      .pipe(
        switchMap((created) =>
          timer(800, 1500).pipe(
            switchMap(() => this.api.export(created.id)),
            filter((e: AnalyticsExportDto) => e.status === 'Succeeded' || e.status === 'Failed'),
            take(1),
            timeout({ first: 120_000 }),
          ),
        ),
      )
      .subscribe({
        next: (e) => {
          if (e.status === 'Failed') {
            this.busy.set(false);
            this.toast.error('Export failed', e.error ?? undefined);
            return;
          }
          this.api.downloadExport(e.id).subscribe({
            next: (blob) => {
              saveBlob(blob, e.fileName ?? `report.${format.toLowerCase()}`);
              this.busy.set(false);
              this.toast.success('Export ready', `${e.fileName} · ${e.rowCount} rows`);
            },
            error: (err: unknown) => {
              this.busy.set(false);
              this.toast.error('Download failed', ApiError.from(err).userMessage);
            },
          });
        },
        error: (err: unknown) => {
          this.busy.set(false);
          if (err instanceof TimeoutError) this.toast.info('Export still running', 'It will appear under Analytics › Exports when ready.');
          else this.toast.error('Could not export', ApiError.from(err).userMessage);
        },
      });
  }
}
