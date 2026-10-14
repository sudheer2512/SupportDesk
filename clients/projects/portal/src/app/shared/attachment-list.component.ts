import { ChangeDetectionStrategy, Component, booleanAttribute, inject, input, signal } from '@angular/core';
import { ApiError, PortalAttachmentDto, PortalContentApi, saveBlob } from '@sd/core';
import { SdFileSizePipe, SdIcon, SdSpinner, ToastService } from '@sd/ui';
import { firstValueFrom } from 'rxjs';
import { errorMessage, fileIcon } from './portal-utils';

/**
 * Downloadable attachment chips. Attachment `url`s are API paths, fetched with the portal session (so
 * private ticket files work) and handed to the browser as a file.
 * `<portal-attachments [attachments]="message.attachments" />`
 */
@Component({
  selector: 'portal-attachments',
  imports: [SdIcon, SdSpinner, SdFileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (attachments().length) {
      <ul class="files" [class.is-inline]="inline()" [attr.aria-label]="label()">
        @for (a of attachments(); track a.id) {
          <li>
            <button
              type="button"
              class="file"
              [attr.aria-label]="'Download ' + a.fileName + ' (' + (a.sizeBytes | sdFileSize) + ')'"
              [attr.aria-busy]="busy() === a.id ? 'true' : null"
              (click)="download(a)"
            >
              <span class="ic" aria-hidden="true"><sd-icon [name]="icon(a)" [size]="15" /></span>
              <span class="name">{{ a.fileName }}</span>
              <span class="size">{{ a.sizeBytes | sdFileSize }}</span>
              @if (busy() === a.id) {
                <sd-spinner [size]="14" />
              } @else {
                <sd-icon name="download" [size]="14" />
              }
            </button>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .files {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .file {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      max-width: 100%;
      min-height: 36px;
      padding: 4px 10px 4px 6px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
      text-align: left;
    }
    .file:hover {
      border-color: var(--sd-primary-border);
      background: var(--sd-primary-soft);
    }
    .file:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .ic {
      display: inline-flex;
      padding: 5px;
      border-radius: var(--sd-radius-sm);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
    }
    .name {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      max-width: 28ch;
      font-weight: var(--sd-weight-medium);
    }
    .size {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      white-space: nowrap;
    }
  `,
})
export class AttachmentListComponent {
  private readonly api = inject(PortalContentApi);
  private readonly toast = inject(ToastService);

  readonly attachments = input.required<PortalAttachmentDto[]>();
  readonly label = input('Attachments');
  readonly inline = input(false, { transform: booleanAttribute });
  protected readonly busy = signal<string | null>(null);

  protected icon(a: PortalAttachmentDto): string {
    return fileIcon(a.contentType);
  }

  protected async download(a: PortalAttachmentDto): Promise<void> {
    if (this.busy()) return;
    this.busy.set(a.id);
    try {
      const blob = await firstValueFrom(this.api.download(a.url));
      saveBlob(blob, a.fileName);
    } catch (err) {
      const e = ApiError.from(err);
      // Network, 5xx, 403 and 429 are already toasted by the global interceptor.
      if (!(e.isNetworkError || e.isServerError || e.status === 403 || e.status === 429)) {
        this.toast.error('Download failed', errorMessage(err, 'The file could not be downloaded.'));
      }
    } finally {
      this.busy.set(null);
    }
  }
}
