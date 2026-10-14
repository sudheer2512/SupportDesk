import { HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, map } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, UploadedFileDto } from '../models/common';
import { UploadEvent } from '../models/files';

/**
 * File uploads (`POST /files`, multipart field `file`) with progress, and authenticated attachment
 * downloads (`GET /attachments/{id}/download`). Uploaded `fileId`s are then referenced by replies,
 * notes and tickets (`attachmentFileIds`).
 */
@Injectable({ providedIn: 'root' })
export class FilesApi {
  private readonly api = inject(ApiClient);

  /** Emits `progress` events and finally `done` with the stored file (scanStatus may still be pending). */
  upload(file: File | Blob, fileName?: string): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', file, fileName ?? (file instanceof File ? file.name : 'upload.bin'));
    return this.api.upload<UploadedFileDto>('files', form, { silent: true }).pipe(
      map((event): UploadEvent | null => {
        if (event.type === HttpEventType.UploadProgress) {
          const total = event.total ?? null;
          return { kind: 'progress', loaded: event.loaded, total, percent: total ? Math.round((event.loaded / total) * 100) : null };
        }
        if (event.type === HttpEventType.Response && event.body) return { kind: 'done', file: event.body };
        return null;
      }),
      filter((e): e is UploadEvent => e !== null),
    );
  }

  /** Same-origin URL of an attachment (needs the bearer token — prefer `download()`). */
  downloadUrl(attachmentId: Guid): string {
    return this.api.url(`attachments/${encodeURIComponent(attachmentId)}/download`);
  }

  download(attachmentId: Guid): Observable<Blob> {
    return this.api.blob(`attachments/${encodeURIComponent(attachmentId)}/download`);
  }

  /** Downloads and hands the blob to the browser as a file save. */
  save(attachmentId: Guid, fileName: string): void {
    this.download(attachmentId).subscribe((blob) => {
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      a.rel = 'noopener';
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    });
  }
}
