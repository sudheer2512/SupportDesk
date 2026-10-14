import { Injectable, computed, inject, signal } from '@angular/core';
import { Preferences } from '@capacitor/preferences';
import { MeDto, PortalMeDto } from '@sd/core';
import { AppMode, ModeService } from './mobile-session';

/** Who was signed in on this device — enough to open cached data offline (never credentials). */
export type SessionSnapshot =
  | {
      kind: 'agent';
      tenantId: string;
      tenantSlug: string;
      tenantName: string;
      userId: string;
      displayName: string;
      email: string;
      permissions: string[];
      isLightAgent: boolean;
    }
  | {
      kind: 'customer';
      brand: string;
      tenantId: string;
      contactId: string;
      fullName: string;
      email: string | null;
      customerName: string | null;
      accessScope: string;
    };

const KEY = (mode: AppMode) => `sd.mobile.snapshot.${mode}`;

/**
 * The last signed-in identity per mode, persisted in Capacitor Preferences. It scopes the offline cache, the
 * outbox and drafts (`scope`), and lets the app open read-only cached views when it starts without a network
 * (the stored refresh token restores the real session as soon as the server is reachable again).
 */
@Injectable({ providedIn: 'root' })
export class SessionSnapshotStore {
  private readonly mode = inject(ModeService);
  private readonly _current = signal<SessionSnapshot | null>(null);
  private loaded: AppMode | null = null;

  readonly current = this._current.asReadonly();

  /** Storage scope of the signed-in identity, e.g. `a:<tenant>:<user>` or `c:<brand>:<contact>`; null when signed out. */
  readonly scope = computed(() => {
    const s = this._current();
    if (!s) return null;
    return s.kind === 'agent' ? `a:${s.tenantId}:${s.userId}` : `c:${s.brand}:${s.contactId}`;
  });

  readonly userId = computed(() => {
    const s = this._current();
    return s?.kind === 'agent' ? s.userId : null;
  });

  readonly contactId = computed(() => {
    const s = this._current();
    return s?.kind === 'customer' ? s.contactId : null;
  });

  /** Loads the snapshot of the current mode (once per mode switch). */
  async load(): Promise<SessionSnapshot | null> {
    const mode = this.mode.mode();
    if (this.loaded === mode) return this._current();
    try {
      const raw = (await Preferences.get({ key: KEY(mode) })).value;
      const parsed = raw ? (JSON.parse(raw) as SessionSnapshot) : null;
      this._current.set(parsed && parsed.kind === mode ? parsed : null);
    } catch {
      this._current.set(null);
    }
    this.loaded = mode;
    return this._current();
  }

  async saveAgent(me: MeDto): Promise<void> {
    await this.save({
      kind: 'agent',
      tenantId: me.tenantId,
      tenantSlug: me.tenantSlug,
      tenantName: me.tenantName,
      userId: me.id,
      displayName: me.displayName,
      email: me.email,
      permissions: me.permissions,
      isLightAgent: me.isLightAgent,
    });
  }

  async saveCustomer(brand: string, contact: PortalMeDto): Promise<void> {
    await this.save({
      kind: 'customer',
      brand: brand.toLowerCase(),
      tenantId: contact.tenantId,
      contactId: contact.id,
      fullName: contact.fullName,
      email: contact.email,
      customerName: contact.customerName,
      accessScope: contact.accessScope,
    });
  }

  async clear(): Promise<void> {
    const mode = this.mode.mode();
    this._current.set(null);
    this.loaded = mode;
    try {
      await Preferences.remove({ key: KEY(mode) });
    } catch {
      /* storage unavailable */
    }
  }

  private async save(snapshot: SessionSnapshot): Promise<void> {
    this._current.set(snapshot);
    this.loaded = snapshot.kind;
    try {
      await Preferences.set({ key: KEY(snapshot.kind), value: JSON.stringify(snapshot) });
    } catch {
      /* storage unavailable — the snapshot still lives for this session */
    }
  }
}
