import { ApiError } from '../http/api-error';

/**
 * Field → message map from a 400 `validation_failed` problem. The API sends `errors` as `{ field: "message" }`
 * (some endpoints `{ field: ["message", …] }`); both shapes are normalised here. Keys are kept as sent (camelCase,
 * nested custom fields as `customFields.key`). Returns `{}` for anything that is not a validation problem.
 */
export function validationMessages(error: unknown): Record<string, string> {
  const err = ApiError.from(error);
  const raw = err.errors as Readonly<Record<string, unknown>>;
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(raw ?? {})) {
    if (typeof value === 'string') result[key] = value;
    else if (Array.isArray(value) && typeof value[0] === 'string') result[key] = value[0];
  }
  return result;
}

/** A user-facing message for a failed save: the first field message for validation errors, otherwise the problem title. */
export function saveErrorMessage(error: unknown): string {
  const err = ApiError.from(error);
  const fields = validationMessages(err);
  const first = Object.values(fields)[0];
  return first ?? err.userMessage;
}
