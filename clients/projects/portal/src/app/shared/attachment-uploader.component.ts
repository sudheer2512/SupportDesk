import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, booleanAttribute, computed, inject, input, model, output, signal, viewChild } from '@angular/core';
import { PortalTicketsApi } from '@sd/core';
import { SdButton, SdFileSizePipe, SdIcon } from '@sd/ui';
import { Subscription } from 'rxjs';
import { BrandService } from '../brand.service';
import { errorMessage, fileIcon } from './portal-utils';

interface UploadItem {
  key: number;
  name: string;
  size: number;
  type: string;
  percent: number | null;
  state: 'uploading' | 'done' | 'error';
  fileId: string | null;
  error: string | null;
}

/**
 * Attachment picker for the signed-in contact: drag & drop or "browse", uploads immediately with a
 * progress bar per file, size-checked against `maxMb`. Two-way `[(fileIds)]` carries the uploaded file
 * ids; `(busyChange)` reports uploads in flight (disable submit meanwhile); `clear()` resets after sending.
 */
@Component({
  selector: 'portal-attachment-uploader',
  imports: [SdIcon, SdButton, SdFileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap" role="group" [attr.aria-label]="label()">
      <div
        class="drop"
        [class.is-over]="over()"
        [class.is-disabled]="disabled()"
        (dragenter)="onDragOver($event)"
        (dragover)="onDragOver($event)"
        (dragleave)="over.set(false)"
        (drop)="onDrop($event)"
      >
        <sd-icon name="paperclip" [size]="16" />
        <span class="drop-text">
          Drag files here or
          <button #chooseBtn sdButton variant="link" type="button" [disabled]="disabled()" (click)="picker.click()">choose files</button>
        </span>
        <span class="drop-hint">Up to {{ maxMb() }} MB each</span>
        <input #picker type="file" multiple class="sd-visually-hidden" tabindex="-1" aria-hidden="true" (change)="onPick($event)" />
      </div>
      @if (items().length) {
        <ul class="items">
          @for (item of items(); track item.key) {
            <li class="item" [class.is-error]="item.state === 'error'">
              <span class="ic" aria-hidden="true"><sd-icon [name]="icon(item)" [size]="15" /></span>
              <div class="info">
                <div class="row">
                  <span class="name">{{ item.name }}</span>
                  <span class="size">{{ item.size | sdFileSize }}</span>
                </div>
                @switch (item.state) {
                  @case ('uploading') {
                    <div
                      class="bar"
                      role="progressbar"
                      [attr.aria-label]="'Uploading ' + item.name"
                      aria-valuemin="0"
                      aria-valuemax="100"
                      [attr.aria-valuenow]="item.percent ?? null"
                    >
                      <span [style.width.%]="item.percent ?? 15" [class.is-indeterminate]="item.percent === null"></span>
                    </div>
                  }
                  @case ('error') {
                    <span class="err">{{ item.error }}</span>
                  }
                  @case ('done') {
                    <span class="ok"><sd-icon name="check" [size]="12" />Attached</span>
                  }
                }
              </div>
              <button type="button" class="remove" [attr.aria-label]="'Remove ' + item.name" (click)="remove(item)">
                <sd-icon name="x" [size]="14" />
              </button>
            </li>
          }
        </ul>
      }
      <p class="sd-visually-hidden" aria-live="polite">{{ announcement() }}</p>
    </div>
  `,
  styles: `
    .wrap {
      display: grid;
      gap: 8px;
    }
    .drop {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 10px;
      padding: 12px 14px;
      border: 1.5px dashed var(--sd-border-strong);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface-2);
      color: var(--sd-text-2);
      font-size: var(--sd-text-sm);
      transition: border-color var(--sd-dur-fast) var(--sd-ease), background-color var(--sd-dur-fast) var(--sd-ease);
    }
    .drop.is-over {
      border-color: var(--sd-primary);
      background: var(--sd-primary-soft);
    }
    .drop.is-disabled {
      opacity: 0.6;
    }
    .drop-text {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .drop-hint {
      margin-left: auto;
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
    }
    .items {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .item {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 8px 8px 10px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
      background: var(--sd-surface);
    }
    .item.is-error {
      border-color: var(--sd-danger-border);
      background: var(--sd-danger-bg);
    }
    .ic {
      display: inline-flex;
      color: var(--sd-text-3);
    }
    .info {
      flex: 1;
      min-width: 0;
      display: grid;
      gap: 4px;
    }
    .row {
      display: flex;
      gap: 8px;
      align-items: baseline;
      min-width: 0;
    }
    .name {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-medium);
    }
    .size {
      color: var(--sd-text-3);
      font-size: var(--sd-text-xs);
      white-space: nowrap;
    }
    .bar {
      height: 4px;
      border-radius: 4px;
      background: var(--sd-surface-3);
      overflow: hidden;
    }
    .bar span {
      display: block;
      height: 100%;
      border-radius: 4px;
      background: var(--sd-primary);
      transition: width var(--sd-dur) var(--sd-ease);
    }
    .bar span.is-indeterminate {
      animation: portal-indeterminate 1.2s var(--sd-ease-in-out) infinite;
    }
    @keyframes portal-indeterminate {
      from {
        transform: translateX(-100%);
      }
      to {
        transform: translateX(700%);
      }
    }
    .err {
      color: var(--sd-danger);
      font-size: var(--sd-text-xs);
    }
    .ok {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      color: var(--sd-success);
      font-size: var(--sd-text-xs);
    }
    .remove {
      display: inline-flex;
      padding: 6px;
      border: 0;
      border-radius: var(--sd-radius-sm);
      background: transparent;
      color: var(--sd-text-3);
      cursor: pointer;
    }
    .remove:hover {
      background: var(--sd-surface-3);
      color: var(--sd-text);
    }
    .remove:focus-visible {
      outline: none;
      box-shadow: var(--sd-focus-ring);
    }
  `,
})
export class AttachmentUploaderComponent {
  private readonly api = inject(PortalTicketsApi);
  private readonly brand = inject(BrandService);

  readonly maxMb = input(20);
  readonly label = input('Attachments');
  readonly disabled = input(false, { transform: booleanAttribute });
  readonly fileIds = model<string[]>([]);
  readonly busyChange = output<boolean>();

  protected readonly items = signal<UploadItem[]>([]);
  protected readonly over = signal(false);
  protected readonly announcement = signal('');
  readonly busy = computed(() => this.items().some((i) => i.state === 'uploading'));

  private readonly chooseButton = viewChild.required('chooseBtn', { read: ElementRef });
  private readonly subscriptions = new Map<number, Subscription>();
  private nextKey = 1;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.subscriptions.forEach((s) => s.unsubscribe()));
  }

  /** Removes every file (after the message/ticket was sent). */
  clear(): void {
    this.subscriptions.forEach((s) => s.unsubscribe());
    this.subscriptions.clear();
    this.items.set([]);
    this.sync();
  }

  protected icon(item: UploadItem): string {
    return fileIcon(item.type);
  }

  protected onDragOver(event: DragEvent): void {
    if (this.disabled() || !event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    this.over.set(true);
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.over.set(false);
    if (this.disabled()) return;
    const files = event.dataTransfer?.files;
    if (files?.length) this.add(Array.from(files));
  }

  protected onPick(event: Event): void {
    const input = event.target as HTMLInputElement;
    if (input.files?.length) this.add(Array.from(input.files));
    input.value = '';
  }

  protected remove(item: UploadItem): void {
    this.subscriptions.get(item.key)?.unsubscribe();
    this.subscriptions.delete(item.key);
    this.items.update((list) => list.filter((i) => i.key !== item.key));
    this.announcement.set(`${item.name} removed`);
    this.sync();
    (this.chooseButton().nativeElement as HTMLElement).focus({ preventScroll: true });
  }

  private add(files: File[]): void {
    const slug = this.brand.slug();
    if (!slug) return;
    const limit = this.maxMb() * 1024 * 1024;
    for (const file of files) {
      const key = this.nextKey++;
      const tooBig = limit > 0 && file.size > limit;
      const item: UploadItem = {
        key,
        name: file.name,
        size: file.size,
        type: file.type,
        percent: tooBig ? null : 0,
        state: tooBig ? 'error' : 'uploading',
        fileId: null,
        error: tooBig ? `Larger than the ${this.maxMb()} MB limit` : null,
      };
      this.items.update((list) => [...list, item]);
      if (tooBig) {
        this.announcement.set(`${file.name} is too large to attach`);
        continue;
      }
      const sub = this.api.upload(slug, file).subscribe({
        next: (event) => {
          if (event.kind === 'progress') this.patch(key, { percent: event.percent });
          else {
            this.patch(key, { state: 'done', percent: 100, fileId: event.file.fileId });
            this.announcement.set(`${file.name} attached`);
          }
        },
        error: (err: unknown) => {
          this.patch(key, { state: 'error', error: errorMessage(err, 'Upload failed') });
          this.announcement.set(`${file.name} could not be uploaded`);
          this.subscriptions.delete(key);
        },
        complete: () => this.subscriptions.delete(key),
      });
      this.subscriptions.set(key, sub);
    }
    this.sync();
  }

  private patch(key: number, changes: Partial<UploadItem>): void {
    this.items.update((list) => list.map((i) => (i.key === key ? { ...i, ...changes } : i)));
    this.sync();
  }

  private sync(): void {
    const ids = this.items()
      .filter((i) => i.state === 'done' && i.fileId)
      .map((i) => i.fileId as string);
    const current = this.fileIds();
    if (ids.length !== current.length || ids.some((id, i) => id !== current[i])) this.fileIds.set(ids);
    this.busyChange.emit(this.busy());
  }
}
