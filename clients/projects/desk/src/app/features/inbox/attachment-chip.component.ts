import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { AttachmentDto, FilesApi } from '@sd/core';
import { SdFileSizePipe, SdIcon } from '@sd/ui';
import { saveBlob } from '../../shared/channel-ui';

/**
 * One message attachment: images render as a thumbnail (fetched with the bearer token into an object URL),
 * other files as a chip. Click/Enter downloads the file.
 */
@Component({
  selector: 'desk-attachment-chip',
  imports: [SdIcon, SdFileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="chip" [class.image]="isImage()" (click)="download()" [attr.aria-label]="'Download ' + attachment().fileName">
      @if (isImage() && preview()) {
        <img [src]="preview()" [alt]="attachment().fileName" />
      } @else {
        <sd-icon [name]="isImage() ? 'image' : 'paperclip'" [size]="14" />
      }
      <span class="name sd-truncate">{{ attachment().fileName }}</span>
      <span class="size sd-xs sd-muted">{{ attachment().sizeBytes | sdFileSize }}</span>
      @if (attachment().isInline) {
        <span class="sd-xs sd-muted">inline</span>
      }
    </button>
  `,
  styles: `
    :host {
      display: inline-block;
      max-width: 100%;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 260px;
      padding: 4px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
      color: var(--sd-text);
      font: inherit;
      font-size: var(--sd-text-sm);
      cursor: pointer;
    }
    .chip:hover {
      background: var(--sd-surface-3);
    }
    .chip:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
    .chip.image {
      flex-direction: column;
      align-items: flex-start;
      padding: 6px;
    }
    img {
      display: block;
      max-width: 220px;
      max-height: 160px;
      border-radius: var(--sd-radius-sm);
      object-fit: cover;
    }
    .name {
      max-width: 180px;
    }
  `,
})
export class AttachmentChipComponent implements OnInit {
  private readonly files = inject(FilesApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly attachment = input.required<AttachmentDto>();

  protected readonly preview = signal<string | null>(null);
  protected readonly isImage = computed(() => this.attachment().contentType.startsWith('image/'));

  ngOnInit(): void {
    if (!this.isImage() || this.attachment().sizeBytes > 5 * 1024 * 1024) return;
    const sub = this.files.download(this.attachment().id).subscribe({
      next: (blob) => this.preview.set(URL.createObjectURL(blob)),
      error: () => this.preview.set(null),
    });
    this.destroyRef.onDestroy(() => {
      sub.unsubscribe();
      const url = this.preview();
      if (url) URL.revokeObjectURL(url);
    });
  }

  protected download(): void {
    this.files.download(this.attachment().id).subscribe((blob) => saveBlob(blob, this.attachment().fileName));
  }
}
