import { ChangeDetectionStrategy, Component, input, model, signal } from '@angular/core';
import { IonButton, IonIcon, IonNote } from '@ionic/angular/standalone';
import { fileSize } from './format';

/**
 * Photo / file attachments for composers. "Camera" opens the device camera (`capture`), "Files" the document picker —
 * plain `<input type=file>` works inside the Capacitor WebView, no camera plugin needed. Files are uploaded when the
 * message is sent (also from the offline outbox).
 * `<m-attachment-picker [(files)]="files" [maxMb]="25" />`
 */
@Component({
  selector: 'm-attachment-picker',
  imports: [IonButton, IonIcon, IonNote],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="actions">
      <ion-button fill="clear" size="small" [disabled]="disabled()" (click)="camera.click()">
        <ion-icon slot="start" name="camera-outline" aria-hidden="true" />Camera
      </ion-button>
      <ion-button fill="clear" size="small" [disabled]="disabled()" (click)="picker.click()">
        <ion-icon slot="start" name="attach-outline" aria-hidden="true" />Files
      </ion-button>
      <input #camera type="file" accept="image/*" capture="environment" hidden aria-label="Take a photo" (change)="add($event)" />
      <input #picker type="file" multiple hidden aria-label="Choose files" (change)="add($event)" />
    </div>
    @if (error()) {
      <ion-note color="danger" role="alert">{{ error() }}</ion-note>
    }
    @if (files().length) {
      <ul class="files" aria-label="Attachments">
        @for (f of files(); track $index) {
          <li>
            <ion-icon [name]="f.type.startsWith('image/') ? 'image-outline' : 'document-outline'" aria-hidden="true" />
            <span class="name">{{ f.name }}</span>
            <span class="size">{{ size(f.size) }}</span>
            <ion-button fill="clear" size="small" color="medium" [attr.aria-label]="'Remove ' + f.name" (click)="remove($index)">
              <ion-icon slot="icon-only" name="close-circle-outline" aria-hidden="true" />
            </ion-button>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .actions {
      display: flex;
      gap: 4px;
    }
    .files {
      list-style: none;
      margin: 4px 0 0;
      padding: 0;
    }
    li {
      display: flex;
      align-items: center;
      gap: 8px;
      min-height: 40px;
      font-size: 0.85rem;
    }
    .name {
      flex: 1;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .size {
      color: var(--sd-text-2);
    }
    ion-note {
      display: block;
      padding: 0 8px 4px;
      font-size: 0.8rem;
    }
  `,
})
export class AttachmentPicker {
  readonly files = model<File[]>([]);
  readonly maxMb = input(25);
  readonly maxFiles = input(10);
  readonly disabled = input(false);

  protected readonly error = signal<string | null>(null);

  protected add(event: Event): void {
    const el = event.target as HTMLInputElement;
    const picked = Array.from(el.files ?? []);
    el.value = '';
    const limit = this.maxMb() * 1024 * 1024;
    const tooBig = picked.filter((f) => f.size > limit);
    const ok = picked.filter((f) => f.size <= limit);
    const next = [...this.files(), ...ok].slice(0, this.maxFiles());
    this.error.set(
      tooBig.length
        ? `${tooBig.map((f) => f.name).join(', ')} ${tooBig.length === 1 ? 'is' : 'are'} larger than ${this.maxMb()} MB.`
        : next.length < this.files().length + ok.length
          ? `At most ${this.maxFiles()} attachments.`
          : null,
    );
    this.files.set(next);
  }

  protected remove(index: number): void {
    this.files.set(this.files().filter((_, i) => i !== index));
    this.error.set(null);
  }

  protected size(bytes: number): string {
    return fileSize(bytes);
  }
}
