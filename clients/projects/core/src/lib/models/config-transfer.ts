import { Guid, IsoDateTime, JsonValue } from './common';

/** Mirrors SupportDesk.Contracts.ConfigTransfer (export / import / snapshots / rollback). */

export interface ConfigSectionInfoDto {
  key: string;
  label: string;
  count: number;
}

export interface ConfigFieldChangeDto {
  field: string;
  current: string | null;
  incoming: string | null;
}

export type ConfigItemStatus = 'new' | 'changed' | 'unchanged';
export type ConfigResolutionAction = 'create' | 'overwrite' | 'skip' | 'rename';

export interface ConfigDiffItemDto {
  /** "section/naturalKey" — the key resolutions refer to. */
  key: string;
  section: string;
  naturalKey: string;
  name: string;
  status: ConfigItemStatus;
  resolution: ConfigResolutionAction;
  newName: string | null;
  changes: ConfigFieldChangeDto[];
}

export interface ConfigDiffSectionDto {
  key: string;
  label: string;
  items: ConfigDiffItemDto[];
}

export interface ConfigDiffDto {
  sections: ConfigDiffSectionDto[];
  new: number;
  changed: number;
  unchanged: number;
  warnings: string[];
}

export interface ConfigDraftDto {
  id: Guid;
  label: string;
  sourceTenant: string | null;
  formatVersion: number;
  status: 'Draft' | 'Published' | 'Discarded';
  createdAt: IsoDateTime;
  createdByName: string | null;
  publishedAt: IsoDateTime | null;
  publishedSnapshotId: Guid | null;
  diff: ConfigDiffDto | null;
}

export interface ImportConfigRequest {
  bundle: JsonValue;
  label?: string | null;
}

export interface ConfigResolutionDto {
  key: string;
  action: ConfigResolutionAction;
  newName?: string | null;
}

export interface ConfigSnapshotDto {
  id: Guid;
  version: number;
  label: string;
  kind: 'Publish' | 'Import' | 'PreRollback' | 'Manual' | 'Rollback';
  createdAt: IsoDateTime;
  createdByName: string | null;
  appliedAt: IsoDateTime | null;
  sections: ConfigSectionInfoDto[];
}

export interface ConfigApplyResultDto {
  created: number;
  updated: number;
  skipped: number;
  deactivated: number;
  warnings: string[];
}

export interface ConfigPublishResultDto {
  draftId: Guid | null;
  snapshot: ConfigSnapshotDto;
  result: ConfigApplyResultDto;
}
