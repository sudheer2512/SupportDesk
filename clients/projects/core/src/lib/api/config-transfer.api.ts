import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, JsonValue } from '../models/common';
import {
  ConfigDraftDto,
  ConfigPublishResultDto,
  ConfigResolutionDto,
  ConfigSectionInfoDto,
  ConfigSnapshotDto,
} from '../models/config-transfer';

const id = encodeURIComponent;

/**
 * Configuration transfer (`/admin/config`, `admin.config_transfer`): export as versioned JSON (ids remapped to natural
 * keys), import into a draft with a diff and per-item resolution, publish (creates a snapshot), snapshots and rollback.
 */
@Injectable({ providedIn: 'root' })
export class ConfigTransferApi {
  private readonly api = inject(ApiClient);

  sections(): Observable<ConfigSectionInfoDto[]> {
    return this.api.get<ConfigSectionInfoDto[]>('admin/config/sections');
  }

  /** The export file (JSON) — optionally limited to some sections. */
  export(sections: string[] = []): Observable<Blob> {
    return this.api.blob('admin/config/export', { params: { sections } });
  }

  import(bundle: JsonValue, label?: string | null): Observable<ConfigDraftDto> {
    return this.api.post<ConfigDraftDto>('admin/config/imports', { bundle, label: label ?? null }, { silent: true });
  }

  drafts(): Observable<ConfigDraftDto[]> {
    return this.api.get<ConfigDraftDto[]>('admin/config/imports');
  }

  draft(draftId: Guid): Observable<ConfigDraftDto> {
    return this.api.get<ConfigDraftDto>(`admin/config/imports/${id(draftId)}`);
  }

  saveResolutions(draftId: Guid, resolutions: ConfigResolutionDto[]): Observable<ConfigDraftDto> {
    return this.api.put<ConfigDraftDto>(`admin/config/imports/${id(draftId)}/resolutions`, { resolutions }, { silent: true });
  }

  publish(draftId: Guid): Observable<ConfigPublishResultDto> {
    return this.api.post<ConfigPublishResultDto>(`admin/config/imports/${id(draftId)}/publish`, {}, { silent: true });
  }

  discard(draftId: Guid): Observable<void> {
    return this.api.delete<void>(`admin/config/imports/${id(draftId)}`);
  }

  snapshots(): Observable<ConfigSnapshotDto[]> {
    return this.api.get<ConfigSnapshotDto[]>('admin/config/snapshots');
  }

  captureSnapshot(label: string | null): Observable<ConfigSnapshotDto> {
    return this.api.post<ConfigSnapshotDto>('admin/config/snapshots', { label });
  }

  downloadSnapshot(snapshotId: Guid): Observable<Blob> {
    return this.api.blob(`admin/config/snapshots/${id(snapshotId)}/download`);
  }

  rollback(snapshotId: Guid, label: string | null = null): Observable<ConfigPublishResultDto> {
    return this.api.post<ConfigPublishResultDto>(`admin/config/snapshots/${id(snapshotId)}/rollback`, { label }, { silent: true });
  }
}
