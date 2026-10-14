import { Injectable, computed, inject, signal } from '@angular/core';
import { MeDto } from '../models/auth';
import { Availability } from '../models/enums';
import { Permission } from './permissions';

/**
 * The signed-in agent/admin as signals. `can()` reads signals, so it is reactive inside templates,
 * computed() and effect(). The UI only hides actions — the API enforces every permission.
 */
@Injectable({ providedIn: 'root' })
export class MeStore {
  private readonly _user = signal<MeDto | null>(null);

  readonly user = this._user.asReadonly();
  readonly isSignedIn = computed(() => this._user() !== null);
  readonly permissions = computed(() => new Set(this._user()?.permissions ?? []));
  readonly planFeatures = computed(() => new Set(this._user()?.planFeatures ?? []));
  readonly availability = computed<Availability>(() => this._user()?.availability ?? 'Offline');
  readonly displayName = computed(() => this._user()?.displayName ?? '');
  readonly timeZone = computed(() => this._user()?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone);
  readonly locale = computed(() => this._user()?.locale ?? globalThis.navigator?.language ?? 'en');

  /** True when the user holds the permission (a '*' grant allows everything). */
  can(permission: Permission | string): boolean {
    const set = this.permissions();
    return set.has(permission) || set.has('*');
  }

  canAny(permissions: readonly (Permission | string)[]): boolean {
    return permissions.length === 0 || permissions.some((p) => this.can(p));
  }

  canAll(permissions: readonly (Permission | string)[]): boolean {
    return permissions.every((p) => this.can(p));
  }

  /** Plan feature flag (subscription), e.g. "ai", "analytics.advanced". */
  hasFeature(feature: string): boolean {
    return this.planFeatures().has(feature);
  }

  set(user: MeDto | null): void {
    this._user.set(user);
  }

  patch(changes: Partial<MeDto>): void {
    this._user.update((u) => (u ? { ...u, ...changes } : u));
  }

  clear(): void {
    this._user.set(null);
  }
}

/** Returns a reactive `can(permission)` function bound to the MeStore (injection context required). */
export function injectCan(): (permission: Permission | string) => boolean {
  const me = inject(MeStore);
  return (permission) => me.can(permission);
}
