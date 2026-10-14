import { UploadedFileDto } from './common';

/** Progress stream emitted by FilesApi.upload(): zero or more `progress` events, then exactly one `done`. */
export type UploadEvent =
  | { kind: 'progress'; loaded: number; total: number | null; percent: number | null }
  | { kind: 'done'; file: UploadedFileDto };
