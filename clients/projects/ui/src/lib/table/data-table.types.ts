import { Directive, TemplateRef, inject, input } from '@angular/core';

/** Column definition for `<sd-data-table>`. */
export interface SdColumn<T> {
  /** Stable id (also the default sort key and the default property read for the cell text). */
  key: string;
  header: string;
  /** Cell text when no `sdCell` template is supplied (defaults to `row[key]`). */
  value?: (row: T) => string | number | boolean | null | undefined;
  sortable?: boolean;
  /** Sort key reported in `sort` (defaults to `key`), e.g. the API's `sort=` value. */
  sortKey?: string;
  /** Fixed width in px. Without it the column is flexible: minmax(minWidth, flex·fr). */
  width?: number;
  minWidth?: number;
  flex?: number;
  align?: 'start' | 'center' | 'end';
  /** Hidden until the user enables it in the column chooser. */
  hiddenByDefault?: boolean;
  /** Always visible, cannot be hidden (e.g. the subject column). */
  locked?: boolean;
}

export interface SdSort {
  key: string;
  dir: 'asc' | 'desc';
}

export interface SdCellContext<T> {
  $implicit: T;
  row: T;
  index: number;
}

/**
 * Custom cell renderer: `<ng-template sdCell="subject" [sdCellRows]="rows()" let-row>…</ng-template>`
 * (`sdCellRows` is optional and only types `row`).
 */
@Directive({ selector: 'ng-template[sdCell]' })
export class SdCellDef<T> {
  readonly column = input.required<string>({ alias: 'sdCell' });
  readonly rows = input<readonly T[] | null | undefined>(undefined, { alias: 'sdCellRows' });
  readonly template = inject<TemplateRef<SdCellContext<T>>>(TemplateRef);

  static ngTemplateContextGuard<T>(_dir: SdCellDef<T>, ctx: unknown): ctx is SdCellContext<T> {
    return true;
  }
}

/** Column visibility/order preferences persisted per table `storageKey`. */
export interface SdTablePrefs {
  order: string[];
  hidden: string[];
}
