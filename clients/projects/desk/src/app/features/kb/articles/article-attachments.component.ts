import { ChangeDetectionStrategy, Component, booleanAttribute, inject, input, model, signal } from '@angular/core';
import { ArticleAttachmentDto, KnowledgeApi, ToastBridge, saveBlob } from '@sd/core';
import { SdButton, SdDialogService, SdFileSizePipe, SdIcon, SdRelativeTimePipe, SdSpinner } from '@sd/ui';
import { errorMessage, toastedGlobally } from '../kb-common';

/**
 * Downloadable files attached to an article (upload, list, download, delete).
 * `<desk-kb-article-attachments [articleId]="id" [(attachments)]="files" [readonly]="!canEdit" />`
 */
@Component({
  selector: 'desk-kb-article-attachments',
  imports: [SdButton, SdIcon, SdSpinner, SdFileSizePipe, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="attachments" aria-labelledby="kb-att-title">
      <header class="head">
        <h3 id="kb-att-title"><sd-icon name="paperclip" [size]="15" /> Attachments <span class="count">{{ attachments().length }}</span></h3>
        @if (!readonly()) {
          <button sdButton size="sm" icon="upload" type="button" [loading]="uploading() > 0" (click)="picker.click()">Attach files</button>
          <input #picker type="file" multiple class="sd-visually-hidden" tabindex="-1" aria-hidden="true" (change)="onPick($event)" />
        }
      </header>
      <p class="sd-visually-hidden" aria-live="polite">{{ status() }}</p>
      @if (attachments().length) {
        <ul class="list">
          @for (a of attachments(); track a.id) {
            <li>
              <sd-icon [name]="a.isImage ? 'image' : 'file-text'" [size]="16" />
              <div class="meta">
                <span class="name sd-truncate" [attr.title]="a.fileName">{{ a.fileName }}</span>
                <span class="sub">{{ a.sizeBytes | sdFileSize }} · added {{ a.createdAt | sdRelativeTime }}</span>
              </div>
              <button
                sdButton
                variant="ghost"
                size="sm"
                icon="download"
                iconOnly
                type="button"
                [loading]="downloading() === a.id"
                [attr.aria-label]="'Download ' + a.fileName"
                (click)="download(a)"
              ></button>
              @if (!readonly()) {
                <button
                  sdButton
                  variant="ghost"
                  size="sm"
                  icon="trash"
                  iconOnly
                  type="button"
                  [loading]="deleting() === a.id"
                  [attr.aria-label]="'Delete ' + a.fileName"
                  (click)="remove(a)"
                ></button>
              }
            </li>
          }
        </ul>
      } @else if (uploading() === 0) {
        <p class="empty">No files attached. Attach PDFs, spreadsheets or other downloads readers may need.</p>
      }
      @if (uploading() > 0) {
        <p class="uploading"><sd-spinner [size]="14" /> Uploading {{ uploading() }} file{{ uploading() === 1 ? '' : 's' }}…</p>
      }
    </section>
  `,
  styles: `
    .attachments {
      display: grid;
      gap: var(--sd-space-2);
      padding: var(--sd-space-4);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--sd-space-2);
    }
    h3 {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: var(--sd-text-md);
      font-weight: var(--sd-weight-semibold);
    }
    .count {
      min-width: 20px;
      padding: 0 6px;
      border-radius: var(--sd-radius-pill);
      background: var(--sd-surface-3);
      color: var(--sd-text-2);
      font-size: var(--sd-text-xs);
      text-align: center;
    }
    .list {
      display: grid;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .list li {
      display: flex;
      align-items: center;
      gap: var(--sd-space-2);
      padding: 6px 8px;
      border: 1px solid var(--sd-divider);
      border-radius: var(--sd-radius-sm);
      color: var(--sd-text-2);
    }
    .meta {
      display: grid;
      flex: 1;
      min-width: 0;
    }
    .name {
      color: var(--sd-text);
      font-weight: var(--sd-weight-medium);
    }
    .sub,
    .empty,
    .uploading {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .uploading {
      display: flex;
      align-items: center;
      gap: 6px;
    }
  `,
})
export class ArticleAttachments {
  readonly articleId = input.required<string>();
  readonly attachments = model<ArticleAttachmentDto[]>([]);
  readonly readonly = input(false, { transform: booleanAttribute });

  private readonly api = inject(KnowledgeApi);
  private readonly toasts = inject(ToastBridge);
  private readonly dialogs = inject(SdDialogService);
  protected readonly uploading = signal(0);
  protected readonly downloading = signal<string | null>(null);
  protected readonly deleting = signal<string | null>(null);
  protected readonly status = signal('');

  protected onPick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    for (const file of files) this.upload(file);
  }

  private upload(file: File): void {
    this.uploading.update((n) => n + 1);
    this.api.uploadAttachment(this.articleId(), file).subscribe({
      next: (attachment) => {
        this.uploading.update((n) => n - 1);
        this.attachments.update((list) => [...list, attachment]);
        this.status.set(`${file.name} attached.`);
      },
      error: (err: unknown) => {
        this.uploading.update((n) => n - 1);
        this.toasts.error(`Could not attach ${file.name}`, errorMessage(err));
      },
    });
  }

  protected download(a: ArticleAttachmentDto): void {
    this.downloading.set(a.id);
    this.api.downloadAttachment(this.articleId(), a.id).subscribe({
      next: (blob) => {
        this.downloading.set(null);
        saveBlob(blob, a.fileName);
      },
      error: (err: unknown) => {
        this.downloading.set(null);
        if (!toastedGlobally(err)) this.toasts.error('Download failed', errorMessage(err));
      },
    });
  }

  protected async remove(a: ArticleAttachmentDto): Promise<void> {
    const ok = await this.dialogs.confirm({ title: `Delete ${a.fileName}?`, message: 'Readers will no longer be able to download it.', confirmLabel: 'Delete', danger: true });
    if (!ok) return;
    this.deleting.set(a.id);
    this.api.deleteAttachment(this.articleId(), a.id).subscribe({
      next: () => {
        this.deleting.set(null);
        this.attachments.update((list) => list.filter((x) => x.id !== a.id));
        this.status.set(`${a.fileName} deleted.`);
      },
      error: (err: unknown) => {
        this.deleting.set(null);
        if (!toastedGlobally(err)) this.toasts.error('Could not delete the file', errorMessage(err));
      },
    });
  }
}
