/**
 * Shared wire types mirroring SupportDesk.Contracts.Common (JSON camelCase, enums as strings,
 * UTC ISO-8601 date strings, GUIDs as strings).
 */

/** A GUID on the wire (lower-case string). */
export type Guid = string;

/** A UTC ISO-8601 timestamp string, e.g. "2026-10-14T08:45:00Z". */
export type IsoDateTime = string;

/** A calendar date (C# DateOnly) on the wire: "YYYY-MM-DD". */
export type IsoDate = string;

/** Any JSON value (C# JsonElement). */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** Keyset-paged list: pass `nextCursor` back as `?cursor=` for the next page; null means no more rows. */
export interface PagedResult<T> {
  items: T[];
  nextCursor: string | null;
  total?: number | null;
}

/** Offset-paged list (small admin lists). */
export interface PageResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface IdName {
  id: Guid;
  name: string;
}

export interface IdNameCode {
  id: Guid;
  name: string;
  code: string | null;
}

export interface FieldChangeDto {
  field: string;
  from: string | null;
  to: string | null;
}

export interface OptionDto {
  value: string;
  label: string;
}

export interface BulkResultDto {
  requested: number;
  succeeded: number;
  failures: BulkFailureDto[];
}

export interface BulkFailureDto {
  id: Guid;
  code: string;
  message: string;
}

export interface CountDto {
  key: string;
  label: string;
  count: number;
}

export interface AttachmentDto {
  id: Guid;
  fileId: Guid;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  visibility: string;
  isInline: boolean;
  scanStatus: string | null;
  createdAt: IsoDateTime;
  downloadUrl: string;
}

export interface UploadedFileDto {
  fileId: Guid;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  scanStatus: string;
}

/** Problem-details `code` extension values (SupportDesk.Contracts.Common.ErrorCodes). */
export type ApiErrorCode =
  | 'validation_failed'
  | 'not_found'
  | 'forbidden'
  | 'conflict'
  | 'concurrency_conflict'
  | 'reply_collision'
  | 'ticket_locked'
  | 'limit_exceeded'
  | 'tenant_suspended'
  | 'approval_required'
  | 'rate_limited'
  | 'invalid_transition'
  | 'missing_required_fields'
  | 'unauthorized';

/** RFC 7807 problem details as returned by the API (extensions are flattened onto the object). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: ApiErrorCode | string;
  traceId?: string;
  /** Validation errors keyed by (camelCase) field name. */
  errors?: Record<string, string[]>;
  [extension: string]: unknown;
}
