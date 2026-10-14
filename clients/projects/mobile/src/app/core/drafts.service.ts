import { Injectable, inject } from '@angular/core';
import { OfflineDb } from './offline-cache.service';
import { SessionSnapshotStore } from './session-snapshot';

interface DraftRow<T> {
  key: string;
  scope: string;
  updatedAt: number;
  value: T;
}

/**
 * Unsent composer content (reply/note text, a half-filled new ticket) kept on the device per identity, so closing the
 * app, losing the network or switching screens never loses what was typed. Saves are debounced per draft.
 */
@Injectable({ providedIn: 'root' })
export class DraftStore {
  private readonly db = inject(OfflineDb).db;
  private readonly session = inject(SessionSnapshotStore);
  private readonly timers = new Map<string, ReturnType<typeof setTimeout>>();

  async get<T>(draftKey: string): Promise<T | null> {
    const scope = this.session.scope();
    if (!scope) return null;
    return (await this.db.get<DraftRow<T>>('drafts', `${scope}|${draftKey}`))?.value ?? null;
  }

  /** Debounced save (400 ms); an empty value removes the draft. */
  save<T>(draftKey: string, value: T | null, isEmpty: (v: T) => boolean = (v) => !v): void {
    const scope = this.session.scope();
    if (!scope) return;
    const key = `${scope}|${draftKey}`;
    const pending = this.timers.get(key);
    if (pending) clearTimeout(pending);
    this.timers.set(
      key,
      setTimeout(() => {
        this.timers.delete(key);
        if (value === null || isEmpty(value)) void this.db.delete('drafts', key);
        else void this.db.put<DraftRow<T>>('drafts', { key, scope, updatedAt: Date.now(), value });
      }, 400),
    );
  }

  async remove(draftKey: string): Promise<void> {
    const scope = this.session.scope();
    if (!scope) return;
    const key = `${scope}|${draftKey}`;
    const pending = this.timers.get(key);
    if (pending) clearTimeout(pending);
    this.timers.delete(key);
    await this.db.delete('drafts', key);
  }

  /** Sign-out: forget the identity's drafts. */
  async clearScope(scope: string): Promise<void> {
    for (const [key, timer] of this.timers) {
      if (key.startsWith(scope + '|')) {
        clearTimeout(timer);
        this.timers.delete(key);
      }
    }
    await this.db.clearScope('drafts', scope);
  }
}
