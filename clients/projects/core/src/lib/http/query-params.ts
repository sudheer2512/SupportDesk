import { HttpParams } from '@angular/common/http';

export type QueryValue = string | number | boolean | Date | null | undefined | readonly (string | number)[];
export type QueryParams = Readonly<Record<string, QueryValue>>;

/**
 * Builds HttpParams from a plain object: skips null/undefined/'' and empty arrays, joins arrays with commas
 * (the API accepts comma-separated lists), serialises Dates as ISO-8601 UTC.
 */
export function toHttpParams(query: QueryParams | object | null | undefined): HttpParams {
  let params = new HttpParams();
  if (!query) return params;
  for (const [key, raw] of Object.entries(query as Record<string, QueryValue>)) {
    if (raw === null || raw === undefined || raw === '') continue;
    if (Array.isArray(raw)) {
      if (raw.length === 0) continue;
      params = params.set(key, raw.join(','));
    } else if (raw instanceof Date) {
      params = params.set(key, raw.toISOString());
    } else {
      params = params.set(key, String(raw));
    }
  }
  return params;
}
