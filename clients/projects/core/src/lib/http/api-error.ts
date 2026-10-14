import { HttpErrorResponse } from '@angular/common/http';
import { ApiErrorCode, ProblemDetails } from '../models/common';
import { ReplyCollisionDto, TicketDetailDto } from '../models/tickets';

/**
 * Typed error thrown by every @sd/core API client. Wraps RFC 7807 problem details (with the `code`
 * extension and, for validation, the `errors` map). `status === 0` means the server was unreachable.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode | string | null;
  readonly title: string;
  readonly detail: string | null;
  readonly errors: Readonly<Record<string, string[]>>;
  readonly problem: ProblemDetails | null;
  readonly traceId: string | null;
  readonly url: string | null;

  constructor(init: {
    status: number;
    title: string;
    detail?: string | null;
    code?: string | null;
    errors?: Record<string, string[]>;
    problem?: ProblemDetails | null;
    url?: string | null;
  }) {
    super(init.detail || init.title);
    this.name = 'ApiError';
    this.status = init.status;
    this.title = init.title;
    this.detail = init.detail ?? null;
    this.code = init.code ?? null;
    this.errors = init.errors ?? {};
    this.problem = init.problem ?? null;
    this.traceId = (init.problem?.traceId as string | undefined) ?? null;
    this.url = init.url ?? null;
  }

  get isNetworkError(): boolean {
    return this.status === 0;
  }

  get isServerError(): boolean {
    return this.status >= 500;
  }

  /** True when the problem's `code` matches. */
  is(code: ApiErrorCode): boolean {
    return this.code === code;
  }

  /** First validation message for a field (keys are matched case-insensitively). */
  fieldError(field: string): string | null {
    const key = Object.keys(this.errors).find((k) => k.toLowerCase() === field.toLowerCase());
    return key ? (this.errors[key]?.[0] ?? null) : null;
  }

  /** A problem-details extension member, e.g. `current` on concurrency_conflict. */
  extension<T>(key: string): T | undefined {
    return this.problem ? (this.problem[key] as T | undefined) : undefined;
  }

  /** 409 concurrency_conflict carries the ticket as it is now in `current`. */
  get currentTicket(): TicketDetailDto | null {
    return this.is('concurrency_conflict') ? (this.extension<TicketDetailDto>('current') ?? null) : null;
  }

  /** 409 reply_collision carries `{currentSeq, newEntries}`. */
  get replyCollision(): ReplyCollisionDto | null {
    if (!this.is('reply_collision')) return null;
    return {
      currentSeq: this.extension<number>('currentSeq') ?? 0,
      newEntries: this.extension<ReplyCollisionDto['newEntries']>('newEntries') ?? [],
    };
  }

  /** User-facing one-liner. */
  get userMessage(): string {
    if (this.isNetworkError) return 'Cannot reach the SupportDesk server. Check your connection and try again.';
    return this.detail || this.title;
  }

  /** Normalises anything thrown by HttpClient (or code) into an ApiError. */
  static from(error: unknown): ApiError {
    if (error instanceof ApiError) return error;
    if (error instanceof HttpErrorResponse) {
      const problem = asProblem(error.error);
      const status = error.status;
      const title =
        problem?.title ||
        (status === 0 ? 'Network error' : error.statusText && error.statusText !== 'OK' ? error.statusText : `HTTP ${status}`);
      return new ApiError({
        status,
        title,
        detail: problem?.detail ?? (typeof error.error === 'string' && error.error.length < 500 ? error.error : null),
        code: problem?.code ?? defaultCode(status),
        errors: problem?.errors,
        problem,
        url: error.url,
      });
    }
    if (error instanceof Error) return new ApiError({ status: -1, title: error.message || 'Unexpected error' });
    return new ApiError({ status: -1, title: 'Unexpected error' });
  }
}

export function isApiError(value: unknown): value is ApiError {
  return value instanceof ApiError;
}

function asProblem(body: unknown): ProblemDetails | null {
  if (body && typeof body === 'object' && !(body instanceof Blob) && !(body instanceof ArrayBuffer)) {
    return body as ProblemDetails;
  }
  if (typeof body === 'string' && body.startsWith('{')) {
    try {
      return JSON.parse(body) as ProblemDetails;
    } catch {
      return null;
    }
  }
  return null;
}

function defaultCode(status: number): ApiErrorCode | null {
  switch (status) {
    case 401:
      return 'unauthorized';
    case 403:
      return 'forbidden';
    case 404:
      return 'not_found';
    case 429:
      return 'rate_limited';
    default:
      return null;
  }
}
